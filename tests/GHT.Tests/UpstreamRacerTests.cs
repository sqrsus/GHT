using System.Net;
using GHT.Services;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// <see cref="UpstreamRacer.RankSeeds"/> 的纯逻辑部分。
/// 真实建连（EWMA / 退避）需要网络，这里只验证「冷启动时不得打乱顺序」和边界条件 ——
/// 一个每天轮换的候选池如果被冷启动随机打乱，轮换的意义就没了。
/// </summary>
public sealed class UpstreamRacerTests
{
    private static LogService NewLog() =>
        new(Path.Combine(Path.GetTempPath(), "ght-racer-" + Guid.NewGuid().ToString("N")));

    private static IPAddress[] Pool(int count) =>
        Enumerable.Range(1, count).Select(i => IPAddress.Parse($"20.200.245.{i}")).ToArray();

    [Fact]
    public void RankSeeds_returns_empty_for_empty_pool()
    {
        using var log = NewLog();
        var racer = new UpstreamRacer(log);

        Assert.Empty(racer.RankSeeds(Array.Empty<IPAddress>(), 4));
    }

    [Fact]
    public void RankSeeds_returns_empty_when_limit_is_zero()
    {
        using var log = NewLog();
        var racer = new UpstreamRacer(log);

        Assert.Empty(racer.RankSeeds(Pool(10), 0));
    }

    [Fact]
    public void RankSeeds_truncates_to_limit()
    {
        using var log = NewLog();
        var racer = new UpstreamRacer(log);

        var picked = racer.RankSeeds(Pool(20), 4);

        Assert.Equal(4, picked.Count);
    }

    [Fact]
    public void RankSeeds_preserves_input_order_before_any_sample_exists()
    {
        using var log = NewLog();
        var racer = new UpstreamRacer(log);
        var pool = Pool(8);

        var picked = racer.RankSeeds(pool, 4);

        // 冷启动时全部为「未知」，必须保持调用方给的顺序（也就是当日轮换顺序）
        Assert.Equal(pool.Take(4), picked);
    }

    [Fact]
    public void RankSeeds_returns_whole_pool_when_limit_exceeds_size()
    {
        using var log = NewLog();
        var racer = new UpstreamRacer(log);
        var pool = Pool(3);

        Assert.Equal(pool, racer.RankSeeds(pool, 32));
    }
}
