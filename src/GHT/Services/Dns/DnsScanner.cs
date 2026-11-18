using System.Diagnostics;
using System.Net;
using GHT.Models;

namespace GHT.Services.Dns;

public sealed record DnsScanProgress(int Completed, int Total, int Succeeded);

/// <summary>
/// 批量扫描公共 DNS：用同一个域名去问清单里的每一台，谁解析得又快又准，就进加速池。
/// 结果按延迟升序返回。
///
/// 并发由 <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
/// 的 <c>MaxDegreeOfParallelism</c> 约束（不是每台服务器开一个任务），因此峰值线程/内存与并发数成正比，
/// 与清单长度无关。进度上报按时间节流：1200 台服务器逐个 Report 会把 UI 线程刷爆。
/// </summary>
public sealed class DnsScanner
{
    /// <summary>进度上报的最小间隔。扫描 1200 台时旧实现会产生 1200 次 UI 调度。</summary>
    private const int ProgressIntervalMs = 120;

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
        var throttle = new ProgressThrottle();

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
                    throttle.Report(progress, done, total, Volatile.Read(ref succeeded));
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

    /// <summary>按时间节流的上报器：只让一个并发线程赢得本次上报，避免同一时刻刷出多条。</summary>
    private sealed class ProgressThrottle
    {
        private long _lastReportTicks;

        public void Report(IProgress<DnsScanProgress>? progress, int done, int total, int succeeded)
        {
            if (progress is null)
            {
                return;
            }

            var now = Environment.TickCount64;
            var last = Volatile.Read(ref _lastReportTicks);

            // 最后一条必须上报，否则进度条会停在 99%
            if (done < total && now - last < ProgressIntervalMs)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last)
            {
                return;
            }

            progress.Report(new DnsScanProgress(done, total, succeeded));
        }
    }
}

