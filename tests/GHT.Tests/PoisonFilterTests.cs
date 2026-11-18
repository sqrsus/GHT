using System.Net;
using GHT.Models;
using GHT.Services;
using GHT.Services.Dns;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 投毒应答必须在<b>入池前</b>被丢掉，而不是留到竞速阶段靠失败来淘汰。
///
/// 这是实测出来的问题：池里某台公共 DNS 对 github.com 返回 127.0.0.1，
/// 另一台返回 198.51.44.8（TEST-NET-2 文档段）。它们「答得很积极」，于是永远排在首批，
/// 而答案永远连不上 —— 一批 8 个竞速名额被这类地址占掉，首连预算就耗在必然失败的地址上。
/// </summary>
public sealed class PoisonFilterTests : IDisposable
{
    private readonly string _directory;
    private readonly LogService _log;

    public PoisonFilterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ght-poison-" + Guid.NewGuid().ToString("N"));
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
            // 后台扫描可能还持有句柄，删不掉不影响结论
        }
    }

    private static AppSettings BuildSettings(params string[] servers)
    {
        return new AppSettings
        {
            DnsPool = servers
                .Select((ip, index) => new DnsPoolEntry { Ip = ip, Name = "server-" + index, LatencyMs = 10 * index })
                .ToList(),
            FirstBatchCollectMs = 500,
            FirstBatchMinIps = 1,
            PoolResolveConcurrency = 4,
            DnsScanTimeoutMs = 2000,
            DnsCacheMinTtlSec = 60,
            DnsCacheMaxTtlSec = 600
        };
    }

    [Fact]
    public async Task Drops_loopback_answer_but_keeps_the_real_one()
    {
        var settings = BuildSettings("10.0.0.1", "10.0.0.2");

        DnsQueryHandler handler = (server, host, timeout, token, ipv6) =>
        {
            if (ipv6)
            {
                return Task.FromResult<DnsQueryResult?>(null);
            }

            // 第一台返回投毒的回环地址，第二台返回真实可路由地址
            var last = server.GetAddressBytes()[3];
            var answer = last == 1
                ? IPAddress.Parse("127.0.0.1")
                : IPAddress.Parse("20.205.243.166");

            return Task.FromResult<DnsQueryResult?>(new DnsQueryResult(new[] { answer }, 60));
        };

        var hub = new ResolverHub(settings, _log, handler);
        var result = await hub.ResolveAsync("github.com", CancellationToken.None);

        Assert.DoesNotContain(IPAddress.Parse("127.0.0.1"), result);
        Assert.Contains(IPAddress.Parse("20.205.243.166"), result);
    }

    [Fact]
    public async Task All_poisoned_answers_yield_no_candidate_at_all()
    {
        var settings = BuildSettings("10.0.0.1", "10.0.0.2");

        var poison = new[]
        {
            IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("0.0.0.0"),
            IPAddress.Parse("198.51.44.8"),
            IPAddress.Parse("192.168.1.10")
        };

        DnsQueryHandler handler = (server, host, timeout, token, ipv6) =>
        {
            if (ipv6)
            {
                return Task.FromResult<DnsQueryResult?>(null);
            }

            var index = server.GetAddressBytes()[3] - 1;
            return Task.FromResult<DnsQueryResult?>(
                new DnsQueryResult(new[] { poison[index % poison.Length] }, 60));
        };

        var hub = new ResolverHub(settings, _log, handler);
        var result = await hub.ResolveAsync("github.com", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Bad_answer_is_attributed_back_to_the_dns_that_gave_it()
    {
        var settings = BuildSettings("10.0.0.2");

        DnsQueryHandler handler = (server, host, timeout, token, ipv6) => Task.FromResult<DnsQueryResult?>(
            ipv6 ? null : new DnsQueryResult(new[] { IPAddress.Parse("20.205.243.166") }, 60));

        var hub = new ResolverHub(settings, _log, handler);
        var result = await hub.ResolveAsync("github.com", CancellationToken.None);

        Assert.Single(result);

        var before = hub.GetServerHealth("10.0.0.2");

        // 模拟竞速判定这个地址连不上
        hub.ReportBadAnswer(result[0]);

        var after = hub.GetServerHealth("10.0.0.2");

        Assert.Equal(before.Ok, after.Ok);
        Assert.True(after.Fail > before.Fail, "答错的 DNS 应当被记上一次失败");
    }

    [Fact]
    public void Unknown_address_does_not_throw()
    {
        var hub = new ResolverHub(BuildSettings("10.0.0.1"), _log);

        // 没有来源记录的地址（例如缓存恢复的）不应引发异常
        hub.ReportBadAnswer(IPAddress.Parse("20.205.243.166"));
        hub.ReportBadAnswer(null);

        Assert.Equal((0, 0), hub.GetServerHealth("10.0.0.1"));
    }
}
