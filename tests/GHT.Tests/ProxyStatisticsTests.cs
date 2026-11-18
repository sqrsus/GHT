using GHT.Models;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 统计口径直接决定用户对「加速有没有生效」的判断，所以每一项都要能算准：
/// 活跃隧道必须能回到 0（否则 UI 会永远显示挂着连接），字节数必须累加而不是覆盖。
/// </summary>
public sealed class ProxyStatisticsTests
{
    [Fact]
    public void Empty_snapshot_is_all_zero()
    {
        var snapshot = new ProxyStatistics().Snapshot();

        Assert.Equal(0, snapshot.Requests);
        Assert.Equal(0, snapshot.BytesUp);
        Assert.Equal(0, snapshot.ActiveTunnels);
        Assert.Equal(string.Empty, snapshot.LastWinner);
    }

    [Fact]
    public void Counts_bytes_in_both_directions()
    {
        var stats = new ProxyStatistics();

        stats.AddBytesUploaded(1024);
        stats.AddBytesUploaded(512);
        stats.AddBytesDownloaded(4096);

        var snapshot = stats.Snapshot();

        Assert.Equal(1536, snapshot.BytesUp);
        Assert.Equal(4096, snapshot.BytesDown);
    }

    [Fact]
    public void Ignores_non_positive_byte_counts()
    {
        var stats = new ProxyStatistics();

        stats.AddBytesUploaded(0);
        stats.AddBytesUploaded(-5);
        stats.AddBytesDownloaded(-1);

        var snapshot = stats.Snapshot();

        Assert.Equal(0, snapshot.BytesUp);
        Assert.Equal(0, snapshot.BytesDown);
    }

    [Fact]
    public void Active_tunnels_return_to_zero_after_close()
    {
        var stats = new ProxyStatistics();

        stats.TunnelOpened();
        stats.TunnelOpened();
        Assert.Equal(2, stats.Snapshot().ActiveTunnels);

        stats.TunnelClosed();
        stats.TunnelClosed();

        var snapshot = stats.Snapshot();
        Assert.Equal(0, snapshot.ActiveTunnels);
    }

    [Fact]
    public void Connect_percentiles_stay_within_observed_range()
    {
        var stats = new ProxyStatistics();

        for (var i = 10; i <= 100; i += 10)
        {
            stats.AddConnectSample(i);
        }

        var snapshot = stats.Snapshot();

        Assert.InRange(snapshot.ConnectP50Ms, 10, 100);
        Assert.InRange(snapshot.ConnectP95Ms, snapshot.ConnectP50Ms, 100);
        Assert.InRange(snapshot.ConnectAvgMs, 10, 100);
    }

    [Fact]
    public void Records_last_winner_and_failure()
    {
        var stats = new ProxyStatistics();

        stats.SetWinner("github.com", "20.205.243.166");
        stats.NoteFailure("api.github.com");

        var snapshot = stats.Snapshot();

        Assert.Contains("github.com", snapshot.LastWinner);
        Assert.Equal("api.github.com", snapshot.LastFailure);
    }
}
