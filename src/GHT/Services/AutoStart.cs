using Microsoft.Win32;

namespace GHT.Services;

/// <summary>开机自启：只写 HKCU\...\Run，无需管理员权限。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GHT";

    public static string ExePath => Environment.ProcessPath ?? string.Empty;

    public static bool Get()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key?.GetValue(ValueName) is not string value)
            {
                return false;
            }

            var path = value.Trim().Trim('"');
            return string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled && !string.IsNullOrEmpty(ExePath))
            {
                key.SetValue(ValueName, $"\"{ExePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表被策略限制时静默失败，UI 会读回真实状态
        }
    }
}
