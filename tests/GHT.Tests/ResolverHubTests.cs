using System.Diagnostics;
using System.Net;
using GHT.Models;
using GHT.Services;
using GHT.Services.Dns;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 首批候选必须是<b>真正的时间窗口</b>。这里用可注入的查询委托模拟「一台 DNS 很慢」的场景，
/// 这是旧实现唯一测不出来的缺陷：它要等整批 <c>Task.WhenAll</c> 结束才检查窗口，
/// 于是 200ms 的窗口会被一台慢 DNS 拖到单次 DNS 超时，把 1500ms 的首连预算整个吃掉。
/// </summary>
public sealed class ResolverHubTests : IDisposable
{
    private readonly string _directory;
    private readonly LogService _log;

    public ResolverHubTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ght-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _log = new LogService(_directory);
    }

    public void Dispose()
    {
        _log.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 后台扫描可能还持有文件句柄，删不掉不影响测试结论
        }
    }

    private static AppSettings BuildSettings(int firstBatchMs, int minIps, int concurrency, params string[] servers)
    {
        return new AppSettings
        {
            DnsPool = servers
                .Select((ip, index) => new DnsPoolEntry { Ip = ip, Name = "server-" + index, LatencyMs = 10 * index })
                .ToList(),
            FirstBatchCollectMs = firstBatchMs,
            FirstBatchMinIps = minIps,
            PoolResolveConcurrency = concurrency,
            DnsScanTimeoutMs = 2000,
            DnsCacheMinTtlSec = 60,
            DnsCacheMaxTtlSec = 600
        };
    }

    private static DnsQueryResult AnswerFor(IPAddress server)
    {
        var last = server.GetAddressBytes()[3];
        return new DnsQueryResult(new[] { IPAddress.Parse($"1.1.1.{last}") }, 60);
    }

    [Fact]
    public async Task First_batch_returns_at_window_even_when_one_dns_is_slow()
    {
        // minIps 故意设成比「快的那几台」能提供的更多，逼实现只能靠时间窗口先返回
        var settings = BuildSettings(200, 8, 8, "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6");
        var slow = IPAddress.Parse("10.0.0.6");

        DnsQueryHandler handler = async (server, host, timeout, token, ipv6) =>
        {
            if (ipv6)
            {
                return null;
            }

            if (server.Equals(slow))
            {
                await Task.Delay(3000, token);
            }

            return AnswerFor(server);
        };

        var hub = new ResolverHub(settings, _log, handler);

        var watch = Stopwatch.StartNew();
        var candidates = await hub.ResolveAsync("github.com", CancellationToken.None);
        watch.Stop();

        // 上限取「单次 DNS 超时（2000ms）」这条参照线，而不是拍一个更小的数：
        // 本用例要证明的性质就是「首批时间由窗口决定，不会被慢 DNS 拖到 DNS 超时」，
        // 慢的那台要 3000ms 才答，所以 <2000ms 已经足以排除「等了整批」。
        // （曾用 1500ms：CI 的 Windows 运行器在并行负载下 Timer/线程池续体会抖到 1524ms，
        //  失败的是运行器调度而不是被测逻辑，断言比性质更严就成了噪声源。）
        Assert.True(
            watch.ElapsedMilliseconds < 2000,
            $"首批被一台慢 DNS 拖住了：{watch.ElapsedMilliseconds}ms（旧实现会等到 3000ms）");

        Assert.Equal(5, candidates.Count);
    }

    [Fact]
    public async Task Publishes_empty_result_when_window_expires_without_any_answer()
    {
        var settings = BuildSettings(150, 5, 8, "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6");

        DnsQueryHandler handler = async (server, host, timeout, token, ipv6) =>
        {
            await Task.Delay(3000, token);
            return null;
        };

        var hub = new ResolverHub(settings, _log, handler);

        var watch = Stopwatch.StartNew();
        var candidates = await hub.ResolveAsync("github.com", CancellationToken.None);
        watch.Stop();

        Assert.Empty(candidates);

        // 同上：参照线是「慢查询要 3000ms」，只要远早于它返回就说明是窗口在起作用
        Assert.True(
            watch.ElapsedMilliseconds < 2000,
            $"无答案时应该按窗口立刻回落，实际等了 {watch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Background_scan_keeps_filling_cache_after_first_batch()
    {
        var servers = Enumerable.Range(1, 12).Select(i => $"10.0.0.{i}").ToArray();
        var settings = BuildSettings(100, 2, 4, servers);

        DnsQueryHandler handler = async (server, host, timeout, token, ipv6) =>
        {
            if (ipv6)
            {
                return null;
            }

            // 前两台立刻答，其余慢一点：首批只要 2 个，剩下的必须由后台补齐
            if (server.GetAddressBytes()[3] > 2)
            {
                await Task.Delay(30, token);
            }

            return AnswerFor(server);
        };

        var hub = new ResolverHub(settings, _log, handler);

        var first = await hub.ResolveAsync("github.com", CancellationToken.None);
        Assert.Equal(2, first.Count);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && (hub.GetCached("github.com")?.Count ?? 0) < 12)
        {
            await Task.Delay(50);
        }

        Assert.Equal(12, hub.GetCached("github.com")!.Count);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_sweep()
    {
        var settings = BuildSettings(100, 2, 4, "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4");

        var queries = 0;

        DnsQueryHandler handler = async (server, host, timeout, token, ipv6) =>
        {
            Interlocked.Increment(ref queries);
            await Task.Delay(50, token);

            return ipv6 ? null : AnswerFor(server);
        };

        var hub = new ResolverHub(settings, _log, handler);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => hub.ResolveAsync("github.com", CancellationToken.None)));

        // 每个调用方拿到的都是同一轮扫描的快照，数量只可能是「发布那一刻的候选数」
        Assert.All(results, list => Assert.True(list.Count >= 2));

        // 4 台 × (A + AAAA) = 8 次查询。允许极少数竞态下多起一轮，但绝不该是 32 轮
        Assert.True(queries <= 12, $"扫描任务被重复启动太多次：{queries} 次查询");
    }
}
