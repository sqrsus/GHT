using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GHT.Models;
using GHT.Services.Dns;

namespace GHT.Services;

/// <summary>
/// 解析中枢：把「加速池里的公共 DNS」变成「某个域名的候选 IP 列表」。
///
/// 关键语义（不要随手简化掉）：
/// 1) 扫描是独立后台任务，<b>不接调用方的取消令牌</b>。调用方因为首连预算超时而放弃等待时，
///    扫描仍会继续并把结果写进缓存——下一次请求就能直接命中。
/// 2) 缓存渐进补充：每解析出一批 IP 就合并进缓存，因此不存在「等全池扫完才有结果」。
/// 3) 加速池为空时返回空列表（而不是抛异常），由上层回落到系统 DNS。
/// </summary>
public sealed class ResolverHub
{
    private readonly LogService _log;
    private readonly DnsResultCache _cache = new();
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<IPAddress>>> _sweeps =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 每台公共 DNS 在运行期的命中/失败计数。UI 用它在加速池里显示「健康 / 降级 / 失效」，
    /// 否则用户只能看到一个扫描时的静态延迟，无法判断这台 DNS 现在还灵不灵。
    /// </summary>
    private readonly ConcurrentDictionary<string, ServerHealth> _serverHealth =
        new(StringComparer.OrdinalIgnoreCase);

    private volatile IReadOnlyList<DnsPoolEntry> _pool = Array.Empty<DnsPoolEntry>();

    /// <summary>加速池版本号。每次换池自增，用来丢弃「换池前发起、换池后才完成」的扫描结果。</summary>
    private int _generation;

    private int _concurrency = 24;
    private int _timeoutMs = 2000;
    private int _minTtl = 60;
    private int _maxTtl = 600;
    private int _firstBatchMs = 200;
    private int _firstBatchMinIps = 5;
    private bool _preferIpv4 = true;
    private long _cacheHits;

    public ResolverHub(AppSettings settings, LogService log)
    {
        _log = log;
        UpdatePool(settings.DnsPool);
        ApplySettings(settings);
    }

    public long CacheHits => Interlocked.Read(ref _cacheHits);

    public int PoolCount => _pool.Count;

    public int CachedDomainCount => _cache.Count;

    /// <summary>取某台公共 DNS 的成功/失败次数（从未参与解析时返回 (0,0)）。</summary>
    public (int Ok, int Fail) GetServerHealth(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !_serverHealth.TryGetValue(ip, out var health))
        {
            return (0, 0);
        }

        return (Volatile.Read(ref health.Ok), Volatile.Read(ref health.Fail));
    }

    private sealed class ServerHealth
    {
        public int Ok;
        public int Fail;
    }

    public void ApplySettings(AppSettings settings)
    {
        _concurrency = Math.Clamp(settings.PoolResolveConcurrency, 1, 256);
        _timeoutMs = Math.Clamp(settings.DnsScanTimeoutMs, 200, 20_000);
        _minTtl = Math.Clamp(settings.DnsCacheMinTtlSec, 5, 86_400);
        _maxTtl = Math.Clamp(settings.DnsCacheMaxTtlSec, _minTtl, 86_400);
        _firstBatchMs = Math.Clamp(settings.FirstBatchCollectMs, 0, 5000);
        _firstBatchMinIps = Math.Clamp(settings.FirstBatchMinIps, 1, 64);
        _preferIpv4 = settings.PreferIpv4;
    }

    public void UpdatePool(IReadOnlyList<DnsPoolEntry>? pool)
    {
        var normalized = (pool ?? Array.Empty<DnsPoolEntry>())
            .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Ip))
            .OrderBy(e => e.LatencyMs)
            .ToArray();

        _pool = normalized;

        // 先加版本号再清缓存：正在跑的扫描会因为这个不匹配而放弃写回，
        // 否则淘汰池的结果会通过缓存继续影响到新池。
        Interlocked.Increment(ref _generation);
        _cache.Clear();
        _log.Info($"加速池已更新：{normalized.Length} 个 DNS");
    }

    public IReadOnlyList<IPAddress>? GetCached(string host) => _cache.Get(host);

    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// 取某个域名的候选 IP。<paramref name="cancellationToken"/> 只影响「等待」，
    /// 不会打断后台扫描（预算超时后仍会补齐缓存）。
    /// </summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var cached = _cache.Get(host);
        if (cached is { Count: > 0 })
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        Task<IReadOnlyList<IPAddress>>? sweep = null;

        if (_sweeps.TryGetValue(host, out var tracked))
        {
            if (tracked.IsCompleted)
            {
                _sweeps.TryRemove(host, out _);
            }
            else
            {
                sweep = tracked;
            }
        }

        sweep ??= _sweeps.GetOrAdd(host, StartSweep);

        return await sweep.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<IReadOnlyList<IPAddress>> StartSweep(string host)
    {
        var firstBatch = new TaskCompletionSource<IReadOnlyList<IPAddress>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var generation = Volatile.Read(ref _generation);
        _ = Task.Run(() => SweepAsync(host, firstBatch, generation), CancellationToken.None);

        return firstBatch.Task;
    }

    private async Task SweepAsync(
        string host,
        TaskCompletionSource<IReadOnlyList<IPAddress>> firstBatch,
        int generation)
    {
        try
        {
            var pool = _pool;
            if (pool.Count == 0)
            {
                _log.Warn($"加速池为空，{host} 将直接走系统 DNS");
                firstBatch.TrySetResult(Array.Empty<IPAddress>());
                return;
            }

            var found = new List<IPAddress>(16);
            var seen = new HashSet<IPAddress>();
            var stopwatch = Stopwatch.StartNew();

            var window = Math.Max(0, _firstBatchMs);
            var minIps = Math.Max(1, _firstBatchMinIps);

            // 硬停阈值：攒够 8 个就不再继续问剩下的 DNS（首连不需要穷举整池）。
            var enoughToStop = Math.Max(minIps, 8);
            var concurrency = Math.Max(1, _concurrency);

            var index = 0;
            var returned = false;

            using var guard = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(Math.Max(4000, _timeoutMs * 8)));

            while (index < pool.Count)
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    // 池在扫描过程中被替换，本轮结果已无意义
                    firstBatch.TrySetResult(Array.Empty<IPAddress>());
                    return;
                }

                var chunk = new List<DnsPoolEntry>(concurrency);
                for (var i = 0; i < concurrency && index < pool.Count; i++)
                {
                    chunk.Add(pool[index++]);
                }

                await Task.WhenAll(
                        chunk.Select(entry => QueryOneAsync(entry, host, found, seen, generation, guard.Token)))
                    .ConfigureAwait(false);

                int count;
                lock (found)
                {
                    count = found.Count;
                }

                if (!returned && (count >= minIps || stopwatch.ElapsedMilliseconds >= window))
                {
                    returned = true;
                    firstBatch.TrySetResult(Snapshot(found));
                }

                if (count >= enoughToStop || stopwatch.ElapsedMilliseconds > 15_000)
                {
                    break;
                }
            }

            firstBatch.TrySetResult(Snapshot(found));
        }
        catch (Exception ex)
        {
            _log.Warn($"解析 {host} 失败：{ex.GetType().Name}: {ex.Message}");
            firstBatch.TrySetResult(Array.Empty<IPAddress>());
        }
    }

    private async Task QueryOneAsync(
        DnsPoolEntry entry,
        string host,
        List<IPAddress> found,
        HashSet<IPAddress> seen,
        int generation,
        CancellationToken token)
    {
        if (!IPAddress.TryParse(entry.Ip, out var server))
        {
            return;
        }

        // A 与 AAAA 同时问：纯 IPv6 环境下只查 A 会得到空答案，候选池直接少一半路子。
        var answers = await Task.WhenAll(
                UdpDnsClient.QueryAsync(server, host, _timeoutMs, token),
                UdpDnsClient.QueryAsync(server, host, _timeoutMs, token, ipv6: true))
            .ConfigureAwait(false);

        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        var addresses = new List<IPAddress>(8);
        var ttl = _maxTtl;
        var hit = false;

        foreach (var answer in answers)
        {
            if (answer is null)
            {
                continue;
            }

            var result = answer.Value;
            if (result.Addresses.Count == 0)
            {
                continue;
            }

            hit = true;
            addresses.AddRange(result.Addresses);
            ttl = Math.Min(ttl, Math.Clamp(result.TtlSeconds, _minTtl, _maxTtl));
        }

        if (!hit)
        {
            RecordHealth(entry.Ip, false);
            return;
        }

        RecordHealth(entry.Ip, true);

        _cache.Merge(host, addresses, ttl, _preferIpv4);

        lock (found)
        {
            foreach (var address in addresses)
            {
                if (seen.Add(address))
                {
                    found.Add(address);
                }
            }

            found.Sort(CompareAddress);
        }
    }

    private void RecordHealth(string ip, bool ok)
    {
        var health = _serverHealth.GetOrAdd(ip, _ => new ServerHealth());

        if (ok)
        {
            Interlocked.Increment(ref health.Ok);
        }
        else
        {
            Interlocked.Increment(ref health.Fail);
        }
    }

    private static IReadOnlyList<IPAddress> Snapshot(List<IPAddress> found)
    {
        lock (found)
        {
            return found.ToArray();
        }
    }

    private static int CompareAddress(IPAddress left, IPAddress right)
    {
        var leftV4 = left.AddressFamily == AddressFamily.InterNetwork;
        var rightV4 = right.AddressFamily == AddressFamily.InterNetwork;

        if (leftV4 != rightV4)
        {
            return leftV4 ? -1 : 1;
        }

        return string.CompareOrdinal(left.ToString(), right.ToString());
    }
}
