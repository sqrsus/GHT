using System.Net;
using System.Net.Sockets;
using GHT.Models;
using GHT.Services;
using GHT.Services.Dns;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 内存上界与回收。这些断言不追求「跑得快」，只守一条：
/// <b>任何一张表都不允许无限增长，任何一条连接都必须被计数并能归还名额</b>。
///
/// 之所以要有这一组：此前的实现里，扫描表、地址健康度、来源映射、日志环形缓冲都只有「软上限」或
/// 干脆没有上限，单机挂一天看起来正常，挂一周就是几百 MB 的稳定增长 —— 这类问题只有靠断言封住。
/// </summary>
public sealed class MemoryBoundsTests : IDisposable
{
    private readonly string _directory;
    private readonly LogService _log;

    public MemoryBoundsTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ght-mem-" + Guid.NewGuid().ToString("N"));
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
            // ignore
        }
    }

    // ── 并发准入 ────────────────────────────────────────────────────────

    [Fact]
    public void Global_connection_limit_rejects_when_saturated_and_recovers_after_release()
    {
        var budget = new ConnectionBudget();

        Assert.True(budget.TryEnter(2));
        Assert.True(budget.TryEnter(2));

        // 第三条必须被拒：这正是「活跃连接无上限」被修掉的地方
        Assert.False(budget.TryEnter(2));
        Assert.Equal(2, budget.Total);
        Assert.Equal(1, budget.RejectedTotal);

        budget.Exit();
        Assert.True(budget.TryEnter(2));

        budget.Exit();
        budget.Exit();
        Assert.Equal(0, budget.Total);
    }

    [Fact]
    public void Zero_limit_means_unbounded()
    {
        var budget = new ConnectionBudget();

        for (var i = 0; i < 100; i++)
        {
            Assert.True(budget.TryEnter(0));
        }

        Assert.Equal(100, budget.Total);
    }

    [Fact]
    public void Per_host_limit_is_independent_and_entries_are_released_to_zero()
    {
        var budget = new ConnectionBudget();

        Assert.True(budget.TryEnterHost("github.com", 1, out var first));
        Assert.False(budget.TryEnterHost("github.com", 1, out _));

        // 另一个主机不受影响：单主机上限不能变成全局上限
        Assert.True(budget.TryEnterHost("api.github.com", 1, out var second));
        Assert.Equal(2, budget.TrackedHosts);

        first.Dispose();
        second.Dispose();

        // 计数归零即从字典移除，字典规模因此不会随「访问过的域名数」增长
        Assert.Equal(0, budget.TrackedHosts);
        Assert.True(budget.TryEnterHost("github.com", 1, out var again));
        again.Dispose();
        Assert.Equal(0, budget.TrackedHosts);
    }

    [Fact]
    public void Rejected_host_lease_is_a_no_op()
    {
        var budget = new ConnectionBudget();

        Assert.True(budget.TryEnterHost("github.com", 1, out var first));
        Assert.False(budget.TryEnterHost("github.com", 1, out var rejected));

        // 拿不到名额时返回的是 default：调用方直接 Dispose 不应改变任何计数
        rejected.Dispose();
        default(ConnectionBudget.HostLease).Dispose();

        Assert.Equal(1, budget.TrackedHosts);

        first.Dispose();
        Assert.Equal(0, budget.TrackedHosts);
    }

    // ── DNS 缓存 ────────────────────────────────────────────────────────

    [Fact]
    public void Addresses_per_domain_are_capped()
    {
        var cache = new DnsResultCache();

        var addresses = Enumerable.Range(1, 200)
            .Select(i => IPAddress.Parse($"1.1.0.{i}"))
            .ToArray();

        cache.Merge("github.com", addresses, 600, preferIpv4: true);

        var stored = cache.Get("github.com");
        Assert.NotNull(stored);
        Assert.Equal(64, stored!.Count);
    }

    [Fact]
    public void Capped_domain_keeps_answering_after_more_merges()
    {
        var cache = new DnsResultCache();

        for (var round = 0; round < 5; round++)
        {
            var batch = Enumerable.Range(1, 200)
                .Select(i => IPAddress.Parse($"2.{round}.0.{i}"))
                .ToArray();

            cache.Merge("github.com", batch, 600, preferIpv4: true);
        }

        var stored = cache.Get("github.com");
        Assert.NotNull(stored);
        Assert.Equal(64, stored!.Count);
    }

    [Fact]
    public void Entry_table_has_a_hard_cap_even_when_nothing_expires()
    {
        // PruneInterval = 0 关掉节流，直接验证「所有条目都新鲜时也有上界」这条性质
        var cache = new DnsResultCache { PruneInterval = 0 };

        for (var i = 1; i <= 700; i++)
        {
            cache.Merge($"host{i}.example.com", new[] { IPAddress.Parse("1.1.1.1") }, 600, preferIpv4: true);
        }

        Assert.True(cache.Count <= 512, $"条目表必须有硬上限，实测 {cache.Count}");
    }

    [Fact]
    public void Repeating_a_known_answer_keeps_the_published_snapshot_stable()
    {
        // 快路径：答案全是已知地址且未过期时，只顺延 TTL，不替换地址数组。
        // 这样既少了三次分配，也不会让读者手上的快照频繁失效。
        var cache = new DnsResultCache();
        cache.Merge("github.com", new[] { IPAddress.Parse("1.1.1.1") }, 600, preferIpv4: true);

        var first = cache.Get("github.com");
        cache.Merge("github.com", new[] { IPAddress.Parse("1.1.1.1") }, 600, preferIpv4: true);
        var second = cache.Get("github.com");

        Assert.Same(first, second);
    }

    // ── 地址健康度 ──────────────────────────────────────────────────────

    [Fact]
    public async Task Address_health_table_is_bounded()
    {
        // 建连用一个立刻成功的假实现：这里要验的是「表会不会涨爆」，不是网络行为
        var racer = new UpstreamRacer(_log, (_, _, _) => Task.FromResult(new TcpClient()));

        for (var i = 0; i < 4200; i++)
        {
            var address = new IPAddress(new byte[] { 10, (byte)(i / 256 % 256), (byte)(i % 256), 1 });
            var win = await racer.RaceAsync(
                new[] { address },
                443,
                connectTimeoutMs: 1000,
                batchBudgetMs: 1000,
                CancellationToken.None);

            Assert.NotNull(win);
            win!.Value.Client.Dispose();
        }

        Assert.True(racer.HealthCount <= 4096, $"地址健康度必须有上限，实测 {racer.HealthCount}");
        Assert.True(racer.HealthCount > 1000, "淘汰不能把有效记录一起清掉");
    }

    [Fact]
    public async Task Saved_health_file_only_contains_recent_entries()
    {
        var racer = new UpstreamRacer(_log, (_, _, _) => Task.FromResult(new TcpClient()));

        for (var i = 0; i < 20; i++)
        {
            var win = await racer.RaceAsync(
                new[] { IPAddress.Parse($"10.9.0.{i + 1}") },
                443,
                connectTimeoutMs: 1000,
                batchBudgetMs: 1000,
                CancellationToken.None);

            win!.Value.Client.Dispose();
        }

        var path = Path.Combine(_directory, "health.json");
        racer.Save(path);

        var text = File.ReadAllText(path);
        Assert.Contains("10.9.0.1", text);
        Assert.Contains("LastSeenMs", text);

        // 写盘是直接在流上完成的：不该留下临时文件
        Assert.False(File.Exists(path + ".tmp"));
    }

    // ── 扫描表 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Completed_sweep_is_removed_from_the_tracking_table()
    {
        var settings = new AppSettings
        {
            DnsPool = new List<DnsPoolEntry>
            {
                new() { Ip = "10.0.0.1", Name = "a", LatencyMs = 1 },
                new() { Ip = "10.0.0.2", Name = "b", LatencyMs = 2 }
            },
            FirstBatchCollectMs = 50,
            FirstBatchMinIps = 1,
            PoolResolveConcurrency = 4,
            DnsScanTimeoutMs = 1000,
            DnsCacheMinTtlSec = 60,
            DnsCacheMaxTtlSec = 600
        };

        DnsQueryHandler handler = (_, _, _, _, ipv6) => Task.FromResult<DnsQueryResult?>(
            ipv6 ? null : new DnsQueryResult(new[] { IPAddress.Parse("1.1.1.1") }, 60));

        var hub = new ResolverHub(settings, _log, handler);

        var candidates = await hub.ResolveAsync("github.com", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(candidates);

        Assert.True(
            await Net.WaitUntilAsync(() => hub.TrackedSweepCount == 0),
            $"扫描结束后必须回收扫描表，实测仍有 {hub.TrackedSweepCount} 项");

        // 缓存命中走的是另一条路径，不该重新建扫描项
        Assert.Equal(1, hub.CachedDomainCount);
        await hub.ResolveAsync("github.com", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, hub.TrackedSweepCount);
    }

    [Fact]
    public async Task Many_distinct_hosts_do_not_accumulate_sweeps()
    {
        var settings = new AppSettings
        {
            DnsPool = new List<DnsPoolEntry> { new() { Ip = "10.0.0.1", Name = "a", LatencyMs = 1 } },
            FirstBatchCollectMs = 0,
            FirstBatchMinIps = 1,
            PoolResolveConcurrency = 4,
            DnsScanTimeoutMs = 1000
        };

        DnsQueryHandler handler = (_, _, _, _, _) =>
            Task.FromResult<DnsQueryResult?>(new DnsQueryResult(new[] { IPAddress.Parse("1.1.1.1") }, 60));

        var hub = new ResolverHub(settings, _log, handler);

        for (var i = 0; i < 200; i++)
        {
            await hub.ResolveAsync($"h{i}.example.com", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(
            await Net.WaitUntilAsync(() => hub.TrackedSweepCount < 20, 5000),
            $"200 个不同域名解析完后扫描表应回落到个位数，实测 {hub.TrackedSweepCount}");
    }

    // ── 日志与指标 ──────────────────────────────────────────────────────

    [Fact]
    public void Recent_log_buffer_is_bounded_by_characters()
    {
        using var log = new TempLog();
        var chunk = new string('x', 3000);

        for (var i = 0; i < 400; i++)
        {
            log.Service.Info($"{i:D4} {chunk}");
        }

        Assert.True(log.Service.RecentChars <= 512 * 1024, $"最近日志必须有字符上限，实测 {log.Service.RecentChars}");
        Assert.True(log.Service.Recent().Count <= 500);
        Assert.True(log.Service.RecentChars > 0);
    }

    [Fact]
    public void Metrics_sample_reflects_limits_and_ratios()
    {
        var metrics = new RuntimeMetrics();

        var first = metrics.Sample(3, 512, 7, 5, 1, 10, 20, 30, 4);
        Assert.Equal(3, first.ActiveConnections);
        Assert.Equal(512, first.MaxConnections);
        Assert.Equal(7, first.RejectedConnections);
        Assert.Equal(5, first.CachedDomains);
        Assert.Equal(30, first.IpSources);
        Assert.True(first.ManagedHeapBytes > 0);
        Assert.True(first.TotalAllocatedBytes > 0);

        // 第一次采样没有速率基线，不应给出一个凭空的分配速率
        Assert.Equal(0, first.AllocRateBytesPerSec);
    }
}
