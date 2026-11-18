using GHT.Services;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 运行环境守卫。断言的是「什么算支持」，不依赖当前机器 —— 否则在 CI 的 Linux 上
/// 一半用例会因为「不是 Windows」直接变成另一个结论。
/// </summary>
public sealed class RuntimeGuardTests
{
    [Fact]
    public void Windows_10_1809_is_supported()
    {
        Assert.True(RuntimeGuard.IsSupported(new Version(10, 0, 17763), isWindows: true, out var reason), reason);
        Assert.Empty(reason);
    }

    [Fact]
    public void Windows_11_is_supported()
    {
        Assert.True(RuntimeGuard.IsSupported(new Version(10, 0, 22631), isWindows: true, out _));
    }

    [Fact]
    public void Windows_10_1607_is_rejected_with_a_readable_reason()
    {
        Assert.False(RuntimeGuard.IsSupported(new Version(10, 0, 14393), isWindows: true, out var reason));

        // 提示必须包含「需要什么」与「当前是什么」，否则用户只能来问
        Assert.Contains("17763", reason);
        Assert.Contains("14393", reason);
    }

    [Fact]
    public void Non_windows_is_rejected()
    {
        Assert.False(RuntimeGuard.IsSupported(new Version(10, 0, 22631), isWindows: false, out var reason));
        Assert.Contains("Windows", reason);
    }

    [Fact]
    public void Description_contains_runtime_and_architecture()
    {
        var text = RuntimeGuard.Describe();

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("NET", text, StringComparison.OrdinalIgnoreCase);
    }
}
