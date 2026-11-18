using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GHT.Services;

/// <summary>
/// Windows 系统代理（HKCU\...\Internet Settings）。只写当前用户，无需管理员权限。
/// </summary>
public static class SystemProxy
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;

    /// <summary>内网与本机地址直连，避免把局域网访问也塞进代理。</summary>
    public const string LocalBypass =
        "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;" +
        "172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";

    public static void Apply(int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
        if (key is null)
        {
            return;
        }

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", LocalBypass, RegistryValueKind.String);
        Refresh();
    }

    /// <summary>只关闭指向本地回环的代理，避免误清用户自己配置的代理。</summary>
    public static void Clear()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
        if (key is null)
        {
            return;
        }

        var server = key.GetValue("ProxyServer") as string;
        if (server is null || !server.StartsWith("127.0.0.1:", StringComparison.Ordinal))
        {
            return;
        }

        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        Refresh();
    }

    public static bool IsApplied(int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: false);
        if (key is null)
        {
            return false;
        }

        var enabled = key.GetValue("ProxyEnable") is int flag && flag != 0;
        var server = key.GetValue("ProxyServer") as string;
        return enabled && string.Equals(server, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase);
    }

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
