using System.Text.Json;
using GHT.Models;

namespace GHT.Services;

/// <summary>
/// settings.json 的读写。
///
/// 写入策略：序列化一次 → 写唯一临时文件 → 覆盖前留 .bak → 原子替换。
/// 用锁串行化，避免 UI 线程与参数失焦事件同时保存时互相覆盖（否则可能出现「端口存了、白名单没存」的半更新）。
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly object SaveGate = new();

    public static AppSettings Load(LogService log)
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return Normalize(new AppSettings());
            }

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (settings is null)
            {
                throw new InvalidDataException("配置内容为空");
            }

            return Normalize(settings);
        }
        catch (Exception ex)
        {
            log.Error("读取配置失败，尝试回退到备份", ex);

            var backup = TryLoadBackup();
            if (backup is not null)
            {
                log.Warn("已从 settings.json.bak 恢复配置");
                return Normalize(backup);
            }

            return Normalize(new AppSettings());
        }
    }

    /// <summary>保存配置。返回 false 表示落盘失败（磁盘满、目录只读、被杀软锁定等）。</summary>
    public static bool Save(AppSettings settings, LogService log)
    {
        lock (SaveGate)
        {
            string? temp = null;

            try
            {
                Directory.CreateDirectory(AppPaths.Root);

                var json = JsonSerializer.Serialize(settings, Options);
                var target = AppPaths.SettingsFile;
                temp = $"{target}.{Guid.NewGuid():N}.tmp";

                File.WriteAllText(temp, json);

                if (File.Exists(target))
                {
                    TryCopy(target, target + ".bak");
                }

                File.Move(temp, target, overwrite: true);
                temp = null;
                return true;
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

    /// <summary>把越界值收敛到合法区间，并补齐可能被外部改坏的空引用。</summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        settings.ProxyPort = Clamp(settings.ProxyPort, 1024, 65535, 7890);
        settings.ConnectBudgetMs = Clamp(settings.ConnectBudgetMs, 0, 60_000, 3000);
        settings.RaceMaxIps = Clamp(settings.RaceMaxIps, 1, 64, 8);
        settings.RaceConnectTimeoutMs = Clamp(settings.RaceConnectTimeoutMs, 200, 60_000, 5000);
        settings.RaceBatchBudgetMs = Clamp(settings.RaceBatchBudgetMs, 200, 120_000, 3000);
        settings.RaceRetryRounds = Clamp(settings.RaceRetryRounds, 0, 5, 2);
        settings.RaceRetryDelayMs = Clamp(settings.RaceRetryDelayMs, 100, 60_000, 800);
        settings.DnsScanConcurrency = Clamp(settings.DnsScanConcurrency, 1, 1024, 128);
        settings.DnsScanTimeoutMs = Clamp(settings.DnsScanTimeoutMs, 200, 20_000, 2000);
        settings.DnsCacheMinTtlSec = Clamp(settings.DnsCacheMinTtlSec, 5, 86_400, 60);
        settings.DnsCacheMaxTtlSec = Clamp(settings.DnsCacheMaxTtlSec, settings.DnsCacheMinTtlSec, 86_400, 600);
        settings.FirstBatchCollectMs = Clamp(settings.FirstBatchCollectMs, 0, 5000, 600);
        settings.FirstBatchMinIps = Clamp(settings.FirstBatchMinIps, 1, 64, 5);
        settings.PoolResolveConcurrency = Clamp(settings.PoolResolveConcurrency, 1, 256, 24);

        settings.GlobalDnsWhitelist ??= string.Empty;
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

    private static AppSettings? TryLoadBackup()
    {
        try
        {
            var backup = AppPaths.SettingsFile + ".bak";
            if (!File.Exists(backup))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(backup), Options);
        }
        catch
        {
            return null;
        }
    }

    private static void TryCopy(string source, string destination)
    {
        try
        {
            File.Copy(source, destination, overwrite: true);
        }
        catch
        {
            // 备份失败不阻断保存
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
