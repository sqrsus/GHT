using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace GHT.Services;

/// <summary>
/// 候选分拣结果。<b>KnownBad</b> 必须单独计数：它和「未验证」是两件完全不同的事 ——
/// 前者是已知不能用（再试纯属浪费预算），后者只是还没试过。
/// </summary>
public readonly record struct CandidateVerdict(
    IReadOnlyList<IPAddress> Trusted,
    IReadOnlyList<IPAddress> Unverified,
    int KnownBad);

/// <summary>
/// 候选地址的 TLS 可用性校验闸门。
///
/// 为什么需要它：竞速只证明 SYN/ACK 回来了，不证明这个地址肯为该主机服务。实测 GitHub 公布的
/// 地址里既有「TCP 能通但 TLS 直接失败」的（20.205.243.160），也有「TLS 正常但返回 404」的
/// Pages 地址。只按 TCP 建连速度选路，就会稳定地把这类地址选成赢家。
///
/// 为什么不在胜出之后再校验：CONNECT 隧道对代理是透明的，客户端自己跟上游做 TLS，
/// 代理既看不到也插不进去。所以只能<b>旁路探测</b>——用 SNI + 证书链独立验证一遍，
/// 把结论缓存起来用于后续请求的候选分拣。
///
/// 为什么首个请求不会因此变慢：冷启动时没有任何已验证地址，此时<b>不</b>阻塞等待探测，
/// 照常把全部候选丢进竞速（与引入本闸门之前完全一致），同时在后台补探测。
/// 探测结论（成功 10 分钟 / 失败 10 分钟）落进缓存后，后续请求才享有「可信优先」。
/// </summary>
public sealed class UpstreamValidator
{
    /// <summary>
    /// 单次探测上限。超过它说明这个地址对我们的意义不大，不必等。
    /// 实测成功的跨境 TLS 握手在 320~490ms，1200ms 已足够覆盖抖动；定得更长只会让一个
    /// 注定失败的探测长期占住 <see cref="MaxConcurrentProbes"/> 的槽位
    /// （日志里大量 3 秒 timeout 就是这么来的）。
    /// </summary>
    private const int ProbeTimeoutMs = 1200;

    /// <summary>同时在跑的探测数。GS 会在一次请求里塞进最多 8 个未验证候选，这里收一下。</summary>
    private const int MaxConcurrentProbes = 4;

    /// <summary>一次 Warm 最多探测多少个候选，避免冷启动瞬间打出一串握手。</summary>
    private const int MaxWarmPerCall = 8;

    /// <summary>
    /// 同一主机两次 Warm 之间的最小间隔。
    /// <see cref="Warm"/> 是每个请求都调的：一个高 QPS 的页面能在几秒内把几百次探测
    /// 排进只有 4 个槽位的队列，结论要几十秒后才落缓存，闸门等于形同虚设。
    /// </summary>
    private const int WarmIntervalMs = 30_000;

    /// <summary>正负结果的缓存时长。地址状态会变，但十分钟内基本稳定。</summary>
    private static readonly TimeSpan DefaultTrustTtl = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DefaultRejectTtl = TimeSpan.FromMinutes(10);

    /// <summary>缓存条目软上限，超过就顺手清一遍过期项（长时间运行才会碰到）。</summary>
    private const int CacheSoftLimit = 4096;

    private readonly LogService _log;
    private readonly TimeSpan _trustTtl;
    private readonly TimeSpan _rejectTtl;
    private readonly Action<IPAddress>? _onReject;
    private readonly ConcurrentDictionary<string, Verdict> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastWarmTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _probeSlots = new(MaxConcurrentProbes, MaxConcurrentProbes);

    private long _probes;
    private long _rejects;

    /// <param name="onReject">
    /// 判定某地址不可用时的回调。生产环境接到 <see cref="UpstreamRacer.ReportReject"/>，
    /// 让「TLS 判坏」这件事进入竞速的健康度，否则同一个地址会被反复选中、反复失败。
    /// </param>
    public UpstreamValidator(LogService log, Action<IPAddress>? onReject = null)
        : this(log, DefaultTrustTtl, DefaultRejectTtl, onReject)
    {
    }

    internal UpstreamValidator(LogService log, TimeSpan trustTtl, TimeSpan rejectTtl, Action<IPAddress>? onReject = null)
    {
        _log = log;
        _trustTtl = trustTtl;
        _rejectTtl = rejectTtl;
        _onReject = onReject;
    }

    private readonly record struct Verdict(bool Ok, long ExpiresTicks, string Reason);

    /// <summary>已经真正跑过的探测次数与其中失败次数，用于确认闸门在工作。</summary>
    public (long Probes, long Rejects) Stats =>
        (Interlocked.Read(ref _probes), Interlocked.Read(ref _rejects));

    /// <summary>
    /// 把候选分成「已验证可用 / 待验证 / 已知不可用」。
    /// 非 443 端口（明文 HTTP）直接全部视为可信：那里没有 TLS 可言，探测无从谈起。
    /// </summary>
    public CandidateVerdict Partition(string host, int port, IReadOnlyList<IPAddress> candidates)
    {
        if (candidates.Count == 0)
        {
            return new CandidateVerdict(Array.Empty<IPAddress>(), Array.Empty<IPAddress>(), 0);
        }

        if (port != 443)
        {
            return new CandidateVerdict(candidates, Array.Empty<IPAddress>(), 0);
        }

        var now = Environment.TickCount64;
        var trusted = new List<IPAddress>(candidates.Count);
        var unverified = new List<IPAddress>(candidates.Count);
        var knownBad = 0;

        foreach (var address in candidates)
        {
            var key = Key(host, address);

            if (_cache.TryGetValue(key, out var verdict) && verdict.ExpiresTicks > now)
            {
                if (verdict.Ok)
                {
                    trusted.Add(address);
                }
                else
                {
                    knownBad++;
                }

                continue;
            }

            unverified.Add(address);
        }

        return new CandidateVerdict(trusted, unverified, knownBad);
    }

    /// <summary>后台补齐：对未验证候选发起探测，结果进缓存，供后续请求使用。</summary>
    public void Warm(string host, int port, IReadOnlyList<IPAddress> candidates, CancellationToken lifetime)
    {
        if (port != 443 || candidates.Count == 0)
        {
            return;
        }

        var now = Environment.TickCount64;
        var last = _lastWarmTicks.GetOrAdd(host, 0L);

        // 0 是「从未 Warm」的哨兵：第一次必须放行，否则开机 30 秒内闸门一直不热身
        if (last != 0 && now - last < WarmIntervalMs)
        {
            return;
        }

        // CAS：并发请求里只有一个能真正发起这一轮探测
        if (!_lastWarmTicks.TryUpdate(host, now, last))
        {
            return;
        }

        var budget = Math.Min(candidates.Count, MaxWarmPerCall);
        for (var i = 0; i < budget; i++)
        {
            _ = ProbeQuietlyAsync(host, candidates[i], port, lifetime);
        }

        PruneIfNeeded();
    }

    /// <summary>
    /// 校验单个候选。命中缓存直接返回；同一 (host, ip) 的在途探测只会真正跑一次。
    /// 这里的 <paramref name="cancellationToken"/> 只影响「等待」，不影响探测本身 ——
    /// 否则首个请求因预算超时而取消，会把已经发出的探测一起废掉，缓存永远建不起来。
    /// </summary>
    public async Task<bool> ValidateAsync(string host, IPAddress address, int port, CancellationToken cancellationToken)
    {
        if (port != 443)
        {
            return true;
        }

        var key = Key(host, address);
        var now = Environment.TickCount64;

        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresTicks > now)
        {
            return cached.Ok;
        }

        var lazy = _inflight.GetOrAdd(
            key,
            _ => new Lazy<Task<bool>>(
                () => ProbeAndRecordAsync(key, host, address, port),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
            {
                Remove(key, lazy);
            }
        }
    }

    private async Task ProbeQuietlyAsync(string host, IPAddress address, int port, CancellationToken cancellationToken)
    {
        try
        {
            await ValidateAsync(host, address, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 后台探测失败不该影响任何调用方
        }
    }

    /// <summary>按 key + value 精确移除，避免删掉并发放入的新任务。</summary>
    private void Remove(string key, Lazy<Task<bool>> lazy)
    {
        var collection = (ICollection<KeyValuePair<string, Lazy<Task<bool>>>>)_inflight;
        collection.Remove(new KeyValuePair<string, Lazy<Task<bool>>>(key, lazy));
    }

    private async Task<bool> ProbeAndRecordAsync(string key, string host, IPAddress address, int port)
    {
        var ok = false;
        var reason = "ok";

        await _probeSlots.WaitAsync().ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(ProbeTimeoutMs);
        var client = new TcpClient(address.AddressFamily);

        try
        {
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);

            using var ssl = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, _, _, errors) => errors == SslPolicyErrors.None);

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    // SNI 必须是目标主机名，否则边缘会给出默认站点证书，校验必然失败
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,

                    // 探测路径上不引入 CRL/OCSP 的网络依赖：那会把一次 3 秒的探测变成不可预测的长尾
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                timeout.Token).ConfigureAwait(false);

            ok = true;
        }
        catch (OperationCanceledException)
        {
            reason = "timeout";
        }
        catch (AuthenticationException ex)
        {
            reason = "tls: " + ex.Message;
        }
        catch (SocketException ex)
        {
            reason = "socket: " + ex.SocketErrorCode;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
        }
        finally
        {
            client.Dispose();
            _probeSlots.Release();
        }

        Record(key, ok, reason);

        Interlocked.Increment(ref _probes);
        if (!ok)
        {
            Interlocked.Increment(ref _rejects);
            _log.Debug($"TLS 校验拒绝 {host} → {address}: {reason}");

            // 回灌给竞速器。不回灌的后果在日志里很直观：20.233.83.149 先以 4ms 的 TCP 建连
            // 赢下竞速、随即被 TLS 判坏，下一次请求它又会因为「建连快」再次胜出。
            try
            {
                _onReject?.Invoke(address);
            }
            catch
            {
                // 回灌失败不该影响探测结论
            }
        }

        return ok;
    }

    /// <summary>写缓存。<paramref name="key"/> 允许测试直接构造，省得为纯缓存逻辑起真实连接。</summary>
    internal void Record(string key, bool ok, string reason)
    {
        var ttl = ok ? _trustTtl : _rejectTtl;
        _cache[key] = new Verdict(ok, Environment.TickCount64 + (long)ttl.TotalMilliseconds, reason);
    }

    /// <summary>测试与调试用：直接给出某主机的校验结论。</summary>
    internal bool? Peek(string host, IPAddress address)
    {
        return _cache.TryGetValue(Key(host, address), out var verdict) ? verdict.Ok : null;
    }

    /// <summary>测试用重载：不经过真实握手，直接把结论写进缓存。</summary>
    internal void Record(string host, IPAddress address, bool ok, string reason = "test") =>
        Record(Key(host, address), ok, reason);

    internal static string Key(string host, IPAddress address) =>
        host.Trim().TrimEnd('.').ToLowerInvariant() + "|" + address;

    private void PruneIfNeeded()
    {
        if (_cache.Count <= CacheSoftLimit)
        {
            return;
        }

        var now = Environment.TickCount64;
        foreach (var pair in _cache)
        {
            if (pair.Value.ExpiresTicks <= now)
            {
                _cache.TryRemove(pair.Key, out _);
            }
        }
    }
}
