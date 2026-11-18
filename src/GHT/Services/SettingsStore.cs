using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GHT.Models;

namespace GHT.Services;

/// <summary>
/// settings.json 的读写。
///
/// 写入策略（顺序不能换）：
/// 1) 序列化一次 → 2) 写唯一临时文件并 <c>Flush(flushToDisk: true)</c> → 3) 备份现有文件 →
/// 4) 原子替换 → 5) 替换失败则用备份恢复。
///
/// 为什么必须落盘再替换：<c>File.WriteAllText</c> 只把数据交给操作系统缓存，强杀进程或断电时
/// 目标文件可能变成「长度已改、内容为零」的半截文件。先写临时文件并强制刷盘，目标文件在被替换前
/// 始终是完整的旧内容，最坏情况也只是丢掉最后一次修改。
///
/// 用锁串行化，避免 UI 线程与参数失焦事件同时保存时互相覆盖（否则可能出现「端口存了、白名单没存」的半更新）。
/// </summary>
public static class SettingsStore
{
    /// <summary>白名单条目上限。超出部分直接丢弃 —— 它是逐请求匹配的，失控的条目数会拖慢每一次请求。</summary>
    public const int MaxWhitelistEntries = 256;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 32
    };

    private static readonly object SaveGate = new();

    /// <summary>配置里合法的字段名。用来识别「未知字段」——它们会被忽略，但要留下痕迹。</summary>
    private static readonly HashSet<string> KnownProperties = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .Select(p => p.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static AppSettings Load(LogService log) => LoadFrom(AppPaths.SettingsFile, log);

    internal static AppSettings LoadFrom(string path, LogService log)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Normalize(new AppSettings());
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var settings = Parse(json, log);
            return Normalize(settings);
        }
        catch (Exception ex)
        {
            log.Error("读取配置失败，尝试回退到备份", ex);

            var backup = TryLoadBackup(path, log);
            if (backup is not null)
            {
                log.Warn("已从 settings.json.bak 恢复配置");
                return Normalize(backup);
            }

            return Normalize(new AppSettings());
        }
    }

    /// <summary>
    /// 严格解析：先校验结构（必须是对象、深度受限），再反序列化。
    /// 未知字段保持「忽略」的兼容策略（旧版本配置可以继续用），但会记一条日志 ——
    /// 否则字段名拼错时用户只会看到「设置不生效」，无从判断原因。
    /// </summary>
    private static AppSettings Parse(string json, LogService log)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"配置根节点必须是对象，实际是 {document.RootElement.ValueKind}");
        }

        var unknown = new List<string>(4);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (KnownProperties.Contains(property.Name))
            {
                continue;
            }

            if (unknown.Count < 8)
            {
                unknown.Add(property.Name);
            }
        }

        if (unknown.Count > 0)
        {
            log.Warn($"配置里有未知字段（已忽略）：{string.Join(", ", unknown)}");
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
        if (settings is null)
        {
            throw new InvalidDataException("配置内容为空");
        }

        return settings;
    }

    /// <summary>保存配置。返回 false 表示落盘失败（磁盘满、目录只读、被杀软锁定等）。</summary>
    public static bool Save(AppSettings settings, LogService log) => SaveTo(settings, log, AppPaths.SettingsFile);

    internal static bool SaveTo(AppSettings settings, LogService log, string target)
    {
        lock (SaveGate)
        {
            string? temp = null;
            var backup = target + ".bak";

            try
            {
                var folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var json = JsonSerializer.Serialize(settings, Options);
                temp = $"{target}.{Guid.NewGuid():N}.tmp";

                WriteDurable(temp, json);

                if (!File.Exists(target))
                {
                    File.Move(temp, target);
                    temp = null;
                    return true;
                }

                // 备份失败不阻断保存，但必须写进日志：
                // 「以为有备份、其实没有」比「明确知道没有备份」危险得多。
                if (!TryBackup(target, backup, log))
                {
                    log.Warn("配置备份失败，本次保存没有可回滚的副本");
                }

                if (TryReplace(temp, target, log))
                {
                    temp = null;
                    return true;
                }

                // 替换失败：此时目标文件可能已被删除，必须把备份放回去，不能留一个空窗。
                if (File.Exists(backup))
                {
                    try
                    {
                        File.Move(backup, target, overwrite: true);
                        log.Warn("保存配置失败，已用备份恢复原配置");
                    }
                    catch (Exception restoreEx)
                    {
                        log.Error("保存配置失败，且用备份恢复也失败", restoreEx);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                log.Error("保存配置失败", ex);
                return false;
            }
            finally
            {
                if (temp is not null)
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
    }

    /// <summary>写临时文件并强制刷盘。这一步是整个原子性的前提，不能换成 File.WriteAllText。</summary>
    private static void WriteDurable(string path, string content)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);

        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);

        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// 原子替换。优先用 <see cref="File.Replace(string, string, string?, bool)"/>（NTFS 上是单次事务性元数据操作），
    /// 在不支持的平台上退化为覆盖移动 —— 两者都保证「目标要么是旧内容、要么是新内容」。
    /// </summary>
    private static bool TryReplace(string temp, string target, LogService log)
    {
        try
        {
            File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            log.Warn($"原子替换不可用（{ex.GetType().Name}），退化为覆盖移动");
        }

        try
        {
            File.Move(temp, target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            log.Error("覆盖移动失败", ex);
            return false;
        }
    }

    private static bool TryBackup(string target, string backup, LogService log)
    {
        try
        {
            File.Copy(target, backup, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"复制备份失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>把越界值收敛到合法区间，并补齐可能被外部改坏的空引用。</summary>
    /// <remarks>
    /// 这里的兜底值一律取自 <see cref="AppSettings"/> 的默认值，而不是再抄一份字面量：
    /// 抄写的那份不会跟着默认值一起改，结果是「默认值调了、越界回退的还是老值」，
    /// 这类不一致在配置文件被手工改坏时最难排查。
    /// </remarks>
    public static AppSettings Normalize(AppSettings settings)
    {
        var defaults = new AppSettings();

        settings.ProxyPort = Clamp(settings.ProxyPort, 1024, 65535, defaults.ProxyPort);
        settings.ConnectBudgetMs = Clamp(settings.ConnectBudgetMs, 0, 60_000, defaults.ConnectBudgetMs);
        settings.RaceMaxIps = Clamp(settings.RaceMaxIps, 1, 64, defaults.RaceMaxIps);
        settings.RaceConnectTimeoutMs = Clamp(settings.RaceConnectTimeoutMs, 200, 60_000, defaults.RaceConnectTimeoutMs);
        settings.RaceBatchBudgetMs = Clamp(settings.RaceBatchBudgetMs, 200, 120_000, defaults.RaceBatchBudgetMs);
        settings.RaceRetryRounds = Clamp(settings.RaceRetryRounds, 0, 5, defaults.RaceRetryRounds);
        settings.RaceRetryDelayMs = Clamp(settings.RaceRetryDelayMs, 100, 60_000, defaults.RaceRetryDelayMs);
        settings.DnsScanConcurrency = Clamp(settings.DnsScanConcurrency, 1, 1024, defaults.DnsScanConcurrency);
        settings.DnsScanTimeoutMs = Clamp(settings.DnsScanTimeoutMs, 200, 20_000, defaults.DnsScanTimeoutMs);
        settings.DnsCacheMinTtlSec = Clamp(settings.DnsCacheMinTtlSec, 5, 86_400, defaults.DnsCacheMinTtlSec);
        settings.DnsCacheMaxTtlSec = Clamp(settings.DnsCacheMaxTtlSec, settings.DnsCacheMinTtlSec, 86_400, defaults.DnsCacheMaxTtlSec);
        settings.FirstBatchCollectMs = Clamp(settings.FirstBatchCollectMs, 0, 5000, defaults.FirstBatchCollectMs);
        settings.FirstBatchMinIps = Clamp(settings.FirstBatchMinIps, 1, 64, defaults.FirstBatchMinIps);
        settings.PoolResolveConcurrency = Clamp(settings.PoolResolveConcurrency, 1, 256, defaults.PoolResolveConcurrency);

        settings.GlobalDnsWhitelist = NormalizeWhitelist(settings.GlobalDnsWhitelist);
        settings.DnsPoolDomain = string.IsNullOrWhiteSpace(settings.DnsPoolDomain) ? "github.com" : settings.DnsPoolDomain.Trim();
        settings.DnsLastScanDomain ??= string.Empty;
        settings.DnsPool ??= new List<DnsPoolEntry>();
        settings.DnsPool = settings.DnsPool
            .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Ip))
            .GroupBy(e => e.Ip, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.LatencyMs)
            .ToList();

        if (settings.CloseAction is not ("Ask" or "Tray" or "Exit"))
        {
            settings.CloseAction = "Ask";
        }

        return settings;
    }

    /// <summary>
    /// 白名单清洗：只接受「主机名」或「*.主机名」。
    ///
    /// 为什么要卡字符集：这份清单决定「哪些域名会走加速池并接受第三方 DNS 的答案」。
    /// 允许路径、端口、通配符出现在任意位置，就等于允许用户写出自己看不懂的匹配规则；
    /// 允许协议前缀则会让「https://x/y」这种写法悄悄匹配不到任何东西。
    /// 出错时静默丢弃（而不是抛异常），因为这是用户手输的配置，UI 会把清洗后的结果回显出来。
    /// </summary>
    private static string NormalizeWhitelist(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var tokens = raw.Split(
            new[] { ',', ';', '\n', '\r', '\t', ' ' },
            StringSplitOptions.RemoveEmptyEntries);

        var kept = new List<string>(Math.Min(tokens.Length, MaxWhitelistEntries));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            var entry = token.Trim().ToLowerInvariant();

            if (!IsValidHostPattern(entry) || !seen.Add(entry))
            {
                continue;
            }

            if (kept.Count >= MaxWhitelistEntries)
            {
                break;
            }

            kept.Add(entry);
        }

        return string.Join(", ", kept);
    }

    /// <summary>校验单个条目：可选 <c>*.</c> 前缀 + 用点分隔的合法主机标签。</summary>
    internal static bool IsValidHostPattern(string entry)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return false;
        }

        var host = entry.StartsWith("*.", StringComparison.Ordinal) ? entry[2..] : entry;

        if (host.Length is 0 or > 253)
        {
            return false;
        }

        // 只接受纯主机名：出现 ':' '/' '@' '#' 等都是用户误贴了 URL
        foreach (var c in host)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
            {
                return false;
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                return false;
            }
        }

        // 至少要有两级（或 localhost），单标签主机名交给系统解析没有意义
        return labels.Length >= 2 || host == "localhost";
    }

    private static AppSettings? TryLoadBackup(string path, LogService log)
    {
        try
        {
            var backup = path + ".bak";
            if (!File.Exists(backup))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(backup, Encoding.UTF8), Options);
        }
        catch (Exception ex)
        {
            log.Warn($"读取配置备份失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static int Clamp(int value, int min, int max, int fallback)
    {
        if (value < min || value > max)
        {
            return fallback;
        }

        return value;
    }
}
