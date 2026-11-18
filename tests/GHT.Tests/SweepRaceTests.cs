using System.Net;
using System.Net.Sockets;
using GHT.Models;
using GHT.Services;
using GHT.Services.Dns;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 扫描期的并发边界。这些问题在单线程下永远看不到，但在真实使用里必然同时发生：
/// 用户在扫描过程中点了「应用加速池」、多个标签页同时请求同一域名、截止计时器和最后一个答案同时到点。
/// </summary>
public sealed class SweepRaceTests : IDisposable
{
    private readonly string _directory;
    private readonly LogService _log;

    public SweepRaceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ght-race-" + Guid.NewGuid().ToString("N"));
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

    private static AppSettings BuildSettings(int firstBatchMs, int minIps, int concurrency, params string[] servers) =>
        new()
        {
            DnsPool = servers
                .Select((ip, index) => new DnsPoolEntry { Ip = ip, Name = "server-" + index, LatencyMs = index })
                .ToList(),
            FirstBatchCollectMs = firstBatchMs,
            FirstBatchMinIps = minIps,
            PoolResolveConcurrency = concurrency,
            DnsScanTimeoutMs = 2000,
            DnsCacheMinTtlSec = 60,
            DnsCacheMaxTtlSec = 600
        };

    private static DnsQueryResult AnswerFor(IPAddress server) =>
        new(new[] { IPAddress.Parse($"1.1.1.{server.GetAddressBytes()[3]}") }, 60);

    [Fact]
    public async Task Pool_change_discards_the_result_of_an_in_flight_sweep()
    {
        var settings = BuildSettings(200, 1, 8, "10.0.0.1", "10.0.0.2");

        // 让所有查询都卡在闸门上，以便在「扫描进行中」换池
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        DnsQueryHandler handler = async (server, host, timeout, token, ipv6) =>
        {
            await gate.Task.WaitAsync(token).ConfigureAwait(false);
            return ipv6 ? null : AnswerFor(server);
        };

        var hub = new ResolverHub(settings, _log, handler);

        var pending = hub.ResolveAsync("github.com", CancellationToken.None);
        await Task.Delay(50);

        // 换池：旧扫描的版本号立即失效
        hub.UpdatePool(new[]
        {
            new DnsPoolEntry { Ip = "10.0.0.9", LatencyMs = 1 }
        });

        gate.SetResult();

        var stale = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(stale);
        Assert.Null(hub.GetCached("github.com"));

        // 新池必须能正常开工，而不是被旧扫描的残留状态挡住
        var fresh = await hub.ResolveAsync("github.com", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(IPAddress.Parse("1.1.1.9"), fresh);
    }

    [Fact]
    public async Task Answers_arriving_exactly_at_the_deadline_publish_once_and_consistently()
    {
        // 窗口与查询耗时相同：截止计时器和最后一个答案会同时到达，发布只能发生一次
        var servers = Enumerable.Range(1, 12).Select(i => $"10.0.0.{i}").ToArray();
        var settings = BuildSettings(120, 3, 8, servers);

        DnsQueryHandler handler = async (server, host, timeout, token, ipv6) =>
        {
            if (ipv6)
            {
                return null;
            }

            await Task.Delay(120, token).ConfigureAwait(false);
            return AnswerFor(server);
        };

        var hub = new ResolverHub(settings, _log, handler);

        var results = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => hub.ResolveAsync("github.com", CancellationToken.None)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        // 所有并发调用方必须拿到同一份快照：如果发布路径有两处，它们会看到不同的列表
        Assert.All(results, list => Assert.Equal(results[0], list));

        // 窗口到点即返回空也是合法结果；但只要发布了非空，就必须是「已经收进来的那批」
        if (results[0].Count > 0)
        {
            Assert.All(results[0], address => Assert.Equal(AddressFamily.InterNetwork, address.AddressFamily));
        }
    }

    [Fact]
    public async Task Repeated_pool_changes_while_resolving_never_throw()
    {
        var settings = BuildSettings(50, 1, 4, "10.0.0.1", "10.0.0.2", "10.0.0.3");

        DnsQueryHandler handler = async (server, host, timeout, token, ipv6) =>
        {
            await Task.Delay(10, token).ConfigureAwait(false);
            return ipv6 ? null : AnswerFor(server);
        };

        var hub = new ResolverHub(settings, _log, handler);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var loops = new List<Task>();

        for (var i = 0; i < 4; i++)
        {
            var index = i;

            loops.Add(Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        await hub.ResolveAsync("github.com", stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }));
        }

        loops.Add(Task.Run(async () =>
        {
            var round = 0;

            while (!stop.IsCancellationRequested)
            {
                round++;
                hub.UpdatePool(new[]
                {
                    new DnsPoolEntry { Ip = $"10.0.1.{round % 5 + 1}", LatencyMs = round }
                });

                await Task.Delay(20).ConfigureAwait(false);
            }
        }));

        // 不抛异常、不死锁就算通过：这里要的是「并发下不会崩」，而不是某个具体数值
        await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
