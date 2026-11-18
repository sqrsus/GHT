using System.Text.Json;
using GHT.Models;

namespace GHT.Services;

/// <summary>settings.json 的读写。写入使用「临时文件 + 覆盖移动」，避免半截文件。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppSettings Load(LogService log)
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return Normalize(new AppSettings());
            }

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            return Normalize(settings);
        }
        catch (Exception ex)
        {
            log.Error("读取配置失败，改用默认配置", ex);
            return Normalize(new AppSettings());
        }
    }

    public static void Save(AppSettings settings, LogService log)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var json = JsonSerializer.Serialize(settings, Options);
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            log.Error("保存配置失败", ex);
        }
    }

    /// <summary>把越界值收敛到合法区间，并补齐可能被外部改坏的空引用。</summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        settings.ProxyPort = Clamp(settings.ProxyPort, 1024, 65535, 7890);
        settings.ConnectBudgetMs = Clamp(settings.ConnectBudgetMs, 0, 60_000, 1500);
        settings.RaceMaxIps = Clamp(settings.RaceMaxIps, 1, 64, 8);
        settings.RaceConnectTimeoutMs = Clamp(settings.RaceConnectTimeoutMs, 200, 60_000, 5000);
        settings.RaceBatchBudgetMs = Clamp(settings.RaceBatchBudgetMs, 200, 120_000, 6000);
        settings.RaceRetryRounds = Clamp(settings.RaceRetryRounds, 0, 5, 2);
        settings.RaceRetryDelayMs = Clamp(settings.RaceRetryDelayMs, 100, 60_000, 800);
        settings.DnsScanConcurrency = Clamp(settings.DnsScanConcurrency, 1, 1024, 128);
        settings.DnsScanTimeoutMs = Clamp(settings.DnsScanTimeoutMs, 200, 20_000, 2000);
        settings.DnsCacheMinTtlSec = Clamp(settings.DnsCacheMinTtlSec, 5, 86_400, 60);
        settings.DnsCacheMaxTtlSec = Clamp(settings.DnsCacheMaxTtlSec, settings.DnsCacheMinTtlSec, 86_400, 600);
        settings.FirstBatchCollectMs = Clamp(settings.FirstBatchCollectMs, 0, 5000, 200);
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

    private static int Clamp(int value, int min, int max, int fallback)
    {
        if (value < min || value > max)
        {
            return fallback;
        }

        return value;
    }
}
