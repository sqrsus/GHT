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
