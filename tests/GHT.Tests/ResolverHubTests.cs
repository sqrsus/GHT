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

        Assert.True(
            watch.ElapsedMilliseconds < 1500,
            $"首批被一台慢 DNS 拖住了：{watch.ElapsedMilliseconds}ms（旧实现会等到 3000ms）");

        Assert.Equal(5, candidates.Count);
    }

    [Fact]
    public async Task Window_expiry_without_any_answer_keeps_waiting_instead_of_publishing_empty()
    {
        // 全部 DNS 都慢：150ms 窗口到点时一个答案都没有。
        //
        // 旧实现在这里<b>发布空数组</b>，等于当场宣布「池里对这个域名没有答案」，上层于是
        // 要么直接回落系统 DNS，要么（注入静态候选时）拿一批未验证的远端地址去把首连预算烧光。
        // 实测日志里 github.com / githubassets / alive.github.com 连续三次「首连预算用尽」
        // 全部由此产生 —— 而它们的 DNS 答案在几百毫秒后其实是有的。
        var settings = BuildSettings(150, 5, 8, "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6");

        DnsQueryHandler handler = async (server, host, timeout, token, ipv6) =>
        {
            if (ipv6)
            {
                return null;
            }

            await Task.Delay(400, token);
            return AnswerFor(server);
        };

        var hub = new ResolverHub(settings, _log, handler);

        var watch = Stopwatch.StartNew();
        var candidates = await hub.ResolveAsync("github.com", CancellationToken.None);
        watch.Stop();

        // 关键断言：必须拿到真实答案，而不是「窗口到点就交白卷」
        Assert.NotEmpty(candidates);

        // 而且应该在首个答案到达时就返回，不能压到整轮扫描结束
        Assert.True(
            watch.ElapsedMilliseconds < 1500,
            $"窗口到期后应尽快返回首个答案，实际等了 {watch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Publishes_empty_only_after_the_whole_sweep_finishes()
    {
        // 整轮扫描确实一个地址都没有 → 这时发布空结果是正确的（上层据此回落系统 DNS）。
        // 这也是<b>唯一</b>允许发布空结果的路径。
        var settings = BuildSettings(150, 5, 8, "10.0.0.1", "10.0.0.2", "10.0.0.3");

        DnsQueryHandler handler = async (server, host, timeout, token, ipv6) =>
        {
            await Task.Delay(200, token);
            return null;
        };

        var hub = new ResolverHub(settings, _log, handler);

        var candidates = await hub.ResolveAsync("github.com", CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Publishes_a_single_answer_after_the_window_expires()
    {
        // github.com 的 A 记录只有 1 个地址，32 台 DNS 答完去重后往往还是 1~2 个，
        // 永远凑不到默认的 FirstBatchMinIps=5。窗口到点后必须改成「有一个发一个」，
        // 否则首批会一直压到整轮扫描结束，等于没有窗口。
        var settings = BuildSettings(100, 5, 8, "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4");

        DnsQueryHandler handler = async (server, host, timeout, token, ipv6) =>
        {
            if (ipv6)
            {
                return null;
            }

            // 所有服务器都只答同一个地址 → 去重后永远只有 1 个
            await Task.Delay(server.GetAddressBytes()[3] == 1 ? 50 : 2000, token);
            return new DnsQueryResult(new[] { IPAddress.Parse("20.205.243.166") }, 60);
        };

        var hub = new ResolverHub(settings, _log, handler);

        var watch = Stopwatch.StartNew();
        var candidates = await hub.ResolveAsync("github.com", CancellationToken.None);
        watch.Stop();

        Assert.Single(candidates);
        Assert.Equal(IPAddress.Parse("20.205.243.166"), candidates[0]);

        // 第 1 台在 50ms 就答了，窗口之后应当立刻发布，而不是等剩下三台的 2000ms 超时
        Assert.True(
            watch.ElapsedMilliseconds < 1000,
            $"首批被慢服务器拖住了：{watch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Drops_unroutable_answers_from_the_pool()
    {
        // 投毒型 DNS 把域名「解析」到 127.0.0.1 / 0.0.0.0 / ::1。这些地址必须进不了候选池。
        var settings = BuildSettings(100, 1, 8, "10.0.0.1", "10.0.0.2", "10.0.0.3");

        DnsQueryHandler handler = (server, host, timeout, token, ipv6) =>
        {
            var last = server.GetAddressBytes()[3];

            DnsQueryResult? result = last switch
            {
                1 => new DnsQueryResult(new[] { IPAddress.Parse("127.0.0.1") }, 60),
                2 => new DnsQueryResult(new[] { IPAddress.Parse("0.0.0.0") }, 60),
                _ => new DnsQueryResult(new[] { IPAddress.Parse("20.205.243.166") }, 60)
            };

            return Task.FromResult(result);
        };

        var hub = new ResolverHub(settings, _log, handler);

        var candidates = await hub.ResolveAsync("github.com", CancellationToken.None);

        Assert.DoesNotContain(IPAddress.Parse("127.0.0.1"), candidates);
        Assert.DoesNotContain(IPAddress.Parse("0.0.0.0"), candidates);
        Assert.Contains(IPAddress.Parse("20.205.243.166"), candidates);
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
