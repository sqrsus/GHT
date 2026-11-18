using System.Diagnostics;
using System.Net;
using GHT.Models;

namespace GHT.Services.Dns;

public sealed record DnsScanProgress(int Completed, int Total, int Succeeded);

/// <summary>
/// 批量扫描公共 DNS：用同一个域名去问清单里的每一台，谁解析得又快又准，就进加速池。
/// 结果按延迟升序返回。
/// </summary>
public sealed class DnsScanner
{
    private readonly LogService _log;

    public DnsScanner(LogService log)
    {
        _log = log;
    }

    public async Task<List<DnsPoolEntry>> ScanAsync(
        string domain,
        int concurrency,
        int timeoutMs,
        IProgress<DnsScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var servers = DnsServerList.Load();
        var total = servers.Count;

        var results = new List<DnsPoolEntry>(total);
        var completed = 0;
        var succeeded = 0;

        _log.Info($"开始扫描 {total} 个公共 DNS，目标域名 {domain}，并发 {concurrency}");

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(concurrency, 1, 1024),
            CancellationToken = cancellationToken
        };

        try
        {
            await Parallel.ForEachAsync(servers, options, async (server, token) =>
            {
                try
                {
                    if (!IPAddress.TryParse(server.Ip, out var address))
                    {
                        return;
                    }

                    var stopwatch = Stopwatch.StartNew();
                    var answer = await UdpDnsClient.QueryAsync(address, domain, timeoutMs, token).ConfigureAwait(false);
                    stopwatch.Stop();

                    if (answer is not null && answer.Value.Addresses.Count > 0)
                    {
                        lock (results)
                        {
                            results.Add(new DnsPoolEntry
                            {
                                Ip = server.Ip,
                                Name = server.Hostname,
                                LatencyMs = (int)stopwatch.ElapsedMilliseconds
                            });
                        }

                        Interlocked.Increment(ref succeeded);
                    }
                }
                catch
                {
                    // 单台失败不影响整轮扫描
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new DnsScanProgress(done, total, Volatile.Read(ref succeeded)));
                }
            });
        }
        catch (OperationCanceledException)
        {
            _log.Warn("DNS 扫描被取消");
        }

        var ordered = results.OrderBy(r => r.LatencyMs).ToList();
        _log.Info($"扫描完成：{ordered.Count} 个 DNS 可用（共 {total} 个）");
        return ordered;
    }
}
