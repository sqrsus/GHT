using System.Runtime.InteropServices;

namespace GHT.Services;

/// <summary>
/// 运行环境守卫。
///
/// 为什么需要显式检查：程序是框架依赖发布，用户机器上「有 .NET 但不是这个」或
/// 「Windows 版本低于 WPF/托盘依赖的下限」时，失败会发生在某个 P/Invoke 或注册表调用里 ——
/// 用户看到的只是一个空白窗口或毫无提示的退出。把「支持什么」写成代码并在启动第一屏就说清楚，
/// 比事后让用户猜要划算得多。
/// </summary>
public static class RuntimeGuard
{
    /// <summary>
    /// 最低 Windows 版本：Windows 10 1809（build 17763）。
    /// .NET 8 桌面运行时自身要求 1607，但托盘图标与注册表读写在这条线上才稳定。
    /// </summary>
    public static readonly Version MinimumWindows = new(10, 0, 17763);

    /// <summary>运行环境摘要。写进日志，便于用户反馈时直接对照。</summary>
    public static string Describe() =>
        $"{RuntimeInformation.OSDescription} / {RuntimeInformation.FrameworkDescription} / {RuntimeInformation.ProcessArchitecture}";

    public static bool IsSupported(out string reason) =>
        IsSupported(Environment.OSVersion.Version, OperatingSystem.IsWindows(), out reason);

    /// <summary>可测形式：版本与平台由调用方给出，避免测试结果依赖当前机器。</summary>
    internal static bool IsSupported(Version osVersion, bool isWindows, out string reason)
    {
        if (!isWindows)
        {
            reason = "GHT 是 Windows 桌面程序，当前系统不是 Windows";
            return false;
        }

        if (osVersion < MinimumWindows)
        {
            reason = $"需要 Windows 10（build {MinimumWindows.Build}）或更高版本，当前为 {osVersion}";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
