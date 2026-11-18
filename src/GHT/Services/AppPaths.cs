namespace GHT.Services;

/// <summary>统一管理 %LOCALAPPDATA%\GHT 下的目录与文件路径，并负责从旧目录一次性迁移配置。</summary>
public static class AppPaths
{
    public const string AppFolderName = "GHT";
    public const string LegacyFolderName = "CloudflareSmartProxy";

    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string LogDirectory => Path.Combine(Root, "logs");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// 地址健康度缓存（失败次数 / 建连耗时 EWMA / 最后失败时间）。
    /// 落盘的意义：健康度只在内存里的话，每次重启都要用首连预算重新试错一遍 ——
    /// 日志里「首连预算用尽」大量集中在启动后几分钟，正是这个原因。
    /// </summary>
    public static string HealthFile => Path.Combine(Root, "address-health.json");

    private static string LegacySettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyFolderName, "settings.json");

    /// <summary>建目录 + 旧配置迁移，幂等。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);

        try
        {
            if (File.Exists(SettingsFile) || !File.Exists(LegacySettingsFile))
            {
                return;
            }

            File.Copy(LegacySettingsFile, SettingsFile, overwrite: false);
        }
        catch
        {
            // 迁移失败不影响启动，后续会写入一份默认配置
        }
    }
}
