using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace GHT.Services;

/// <summary>
/// Windows 系统代理（HKCU\...\Internet Settings）。只写当前用户，无需管理员权限。
///
/// 崩溃恢复：进程被强杀时没有机会执行清理，系统代理会一直指向已关闭的端口，导致全机断网。
/// 因此接管前先把用户原本的 ProxyEnable / ProxyServer / ProxyOverride 快照到 proxy-backup.json，
/// 正常退出时按快照还原；下次启动若发现快照残留且系统代理仍指向自己，则自动还原。
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

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed class ProxySnapshot
    {
        public int ProxyEnable { get; set; }

        public string? ProxyServer { get; set; }

        public string? ProxyOverride { get; set; }

        /// <summary>快照时我们使用的端口，用于确认当前代理确实是「我们」留下的。</summary>
        public int AppliedPort { get; set; }

        public DateTimeOffset CapturedAt { get; set; }
    }

    private static string BackupFile => Path.Combine(AppPaths.Root, "proxy-backup.json");

    public static void Apply(int port)
    {
        lock (Gate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
            if (key is null)
            {
                return;
            }

            CaptureIfNeeded(key, port);

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
            key.SetValue("ProxyOverride", LocalBypass, RegistryValueKind.String);
        }

        Refresh();
    }

    /// <summary>
    /// 关闭代理。若存在快照且当前代理确实是我们设的，则完整还原用户原设置；
    /// 否则只关掉指向本地回环的那一项，绝不覆盖用户自己配置的代理。
    /// </summary>
    public static bool Clear()
    {
        var changed = false;

        lock (Gate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
            if (key is null)
            {
                return false;
            }

            var snapshot = TryLoadSnapshot();
            var server = key.GetValue("ProxyServer") as string;

            if (snapshot is not null && IsOurServer(server, snapshot.AppliedPort))
            {
                Restore(key, snapshot);
                DeleteSnapshot();
                changed = true;
            }
            else if (IsLoopbackServer(server))
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                changed = true;

                if (snapshot is not null)
                {
                    // 端口对不上，快照已失去意义
                    DeleteSnapshot();
                }
            }
            else if (snapshot is not null)
            {
                // 用户已经手工改成了别的代理，不要再动它
                DeleteSnapshot();
            }
        }

        if (changed)
        {
            Refresh();
        }

        return changed;
    }

    /// <summary>
    /// 启动时的残留恢复：有快照，且系统代理仍指向快照记录的端口，说明上次是异常退出。
    /// 返回 true 表示执行了还原。
    /// </summary>
    public static bool Recover()
    {
        var recovered = false;

        lock (Gate)
        {
            var snapshot = TryLoadSnapshot();
            if (snapshot is null)
            {
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
            if (key is null)
            {
                DeleteSnapshot();
                return false;
            }

            var server = key.GetValue("ProxyServer") as string;
            if (!IsOurServer(server, snapshot.AppliedPort))
            {
                // 残留快照与当前代理无关（用户改过，或系统已恢复），直接丢弃
                DeleteSnapshot();
                return false;
            }

            Restore(key, snapshot);
            DeleteSnapshot();
            recovered = true;
        }

        if (recovered)
        {
            Refresh();
        }

        return recovered;
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
        return enabled && IsOurServer(server, port);
    }

    private static void CaptureIfNeeded(RegistryKey key, int port)
    {
        try
        {
            if (File.Exists(BackupFile))
            {
                return;
            }

            var snapshot = new ProxySnapshot
            {
                ProxyEnable = key.GetValue("ProxyEnable") is int flag ? flag : 0,
                ProxyServer = key.GetValue("ProxyServer") as string,
                ProxyOverride = key.GetValue("ProxyOverride") as string,
                AppliedPort = port,
                CapturedAt = DateTimeOffset.Now
            };

            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(BackupFile, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch
        {
            // 快照失败不阻断代理启动，只是失去崩溃恢复能力
        }
    }

    private static void Restore(RegistryKey key, ProxySnapshot snapshot)
    {
        key.SetValue("ProxyEnable", snapshot.ProxyEnable, RegistryValueKind.DWord);

        if (string.IsNullOrEmpty(snapshot.ProxyServer))
        {
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("ProxyServer", snapshot.ProxyServer, RegistryValueKind.String);
        }

        if (string.IsNullOrEmpty(snapshot.ProxyOverride))
        {
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("ProxyOverride", snapshot.ProxyOverride, RegistryValueKind.String);
        }
    }

    private static ProxySnapshot? TryLoadSnapshot()
    {
        try
        {
            if (!File.Exists(BackupFile))
            {
                return null;
            }

            var json = File.ReadAllText(BackupFile);
            return JsonSerializer.Deserialize<ProxySnapshot>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteSnapshot()
    {
        try
        {
            if (File.Exists(BackupFile))
            {
                File.Delete(BackupFile);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsOurServer(string? server, int port) =>
        server is not null
        && string.Equals(server.Trim(), $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopbackServer(string? server) =>
        server is not null && server.Trim().StartsWith("127.0.0.1:", StringComparison.Ordinal);

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
