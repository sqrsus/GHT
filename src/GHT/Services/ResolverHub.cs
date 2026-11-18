using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GHT.Models;
using GHT.Services.Dns;

namespace GHT.Services;

/// <summary>
/// DNS 查询委托。生产环境固定是 <c>UdpDnsClient.QueryAsync</c>；
/// 测试用它可以精确控制「哪台 DNS 慢、慢多久」，从而验证首批窗口是否真的是时间窗口。
/// </summary>
internal delegate Task<DnsQueryResult?> DnsQueryHandler(
    IPAddress server,
    string host,
    int timeoutMs,
    CancellationToken cancellationToken,
    bool ipv6);

/// <summary>
/// 解析中枢：把「加速池里的公共 DNS」变成「某个域名的候选 IP 列表」。
///
/// 关键语义（不要随手简化掉）：
/// 1) 扫描是独立后台任务，<b>不接调用方的取消令牌</b>。调用方因为首连预算超时而放弃等待时，
///    扫描仍会继续并把结果写进缓存——下一次请求就能直接命中。
/// 2) 缓存渐进补充：每解析出一批 IP 就合并进缓存，因此不存在「等全池扫完才有结果」。
/// 3) 加速池为空时返回空列表（而不是抛异常），由上层回落到系统 DNS。
/// 4) 首批发布是<b>真正的时间窗口</b>：由「任一查询完成」或「独立截止计时器」触发，
///    不依赖某一批 <c>Task.WhenAll</c> 结束。旧实现要等整批 DNS 都返回才检查窗口，
///    只要批里有一台慢，首批就被拖到单次 DNS 超时附近，把整个首连预算吃掉。
/// </summary>
public sealed class ResolverHub
{
    /// <summary>首批最多问多少台 DNS。收益来自「尽快拿到几个可用 IP」，不是把整池问完。</summary>
    private const int FirstBatchServerCap = 32;

    /// <summary>后台补齐的目标候选数。到量即停，避免为一个域名把上千台 DNS 全问一遍。</summary>
    private const int BackgroundTargetIps = 16;

    private readonly LogService _log;
    private readonly DnsQueryHandler _query;
    private readonly DnsResultCache _cache = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<IPAddress>>>> _sweeps =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 每台公共 DNS 在运行期的命中/失败计数。UI 用它在加速池里显示「健康 / 降级 / 失效」，
    /// 否则用户只能看到一个扫描时的静态延迟，无法判断这台 DNS 现在还灵不灵。
    /// 首批也会用它把「失败多于成功」的服务器排到后面。
    /// </summary>
    private readonly ConcurrentDictionary<string, ServerHealth> _serverHealth =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 候选 IP → 首个给出它的那台 DNS。
    ///
    /// 为什么需要这张表：健康度原本只区分「能答 / 不能答」，但真正有害的是<b>能答却答错</b> ——
    /// 实测池里有 DNS 对 github.com 返回 127.0.0.1、198.51.44.8 这类地址，它们「答得很积极」，
    /// 于是永远排在首批，而答案永远连不上。有了 IP 到来源的映射，竞速失败时才能把责任
    /// 精确记到那台 DNS 上，而不是笼统地让所有 DNS 一起背锅。
    /// </summary>
    private readonly ConcurrentDictionary<IPAddress, string> _ipSource = new();

    /// <summary>映射表的软上限。地址集合本身有限，这里只是防止长期运行后无限增长。</summary>
    private const int IpSourceSoftLimit = 8192;

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
        : this(settings, log, null)
    {
    }

    /// <summary>测试构造：注入查询委托，以便控制单台 DNS 的响应时间。</summary>
    internal ResolverHub(AppSettings settings, LogService log, DnsQueryHandler? query)
    {
        _log = log;
        _query = query ?? UdpDnsClient.QueryAsync;

        UpdatePool(settings.DnsPool);
        ApplySettings(settings);
    }

    public long CacheHits => Interlocked.Read(ref _cacheHits);

    public int PoolCount => _pool.Count;

    public int CachedDomainCount => _cache.Count;

    /// <summary>取某台公共 DNS 的成功/失败次数（从未参与解析时返回 (0,0)）。</summary>
    /// <remarks>「答了但答案连不上」也计入失败，因为对使用者而言它与解析失败没有区别。</remarks>
    public (int Ok, int Fail) GetServerHealth(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !_serverHealth.TryGetValue(ip, out var health))
        {
            return (0, 0);
        }

        return (
            Volatile.Read(ref health.Ok),
            Volatile.Read(ref health.Fail) + Volatile.Read(ref health.Bad));
    }

    /// <summary>
    /// 竞速判定某个候选地址连不上时回灌：把责任记到「给出这个答案的那台 DNS」上。
    ///
    /// 只影响排序，不影响可用性 —— 被记过的 DNS 仍会参与解析，只是排到后面。
    /// 这是刻意的：池里如果全是劣质 DNS，直接剔除会让人彻底连不上，降权则只是「先用好的」。
    /// </summary>
    public void ReportBadAnswer(IPAddress? address)
    {
        if (address is null)
        {
            return;
        }

        if (!_ipSource.TryGetValue(address, out var server) || string.IsNullOrEmpty(server))
        {
            return;
        }

        var health = _serverHealth.GetOrAdd(server, _ => new ServerHealth());
        Interlocked.Increment(ref health.Bad);
    }

    private sealed class ServerHealth
    {
        public int Ok;
        public int Fail;

        /// <summary>「答了，但答案连不上」的次数。与 Fail 分开计，因为两者含义不同：</summary>
        /// <remarks>Fail 是答不出（网络不通/超时），Bad 是答了但答错（投毒、污染、过时记录）。</remarks>
        public int Bad;
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
            if (tracked.Value.IsCompleted)
            {
                RemoveSweep(host, tracked);
            }
            else
            {
                sweep = tracked.Value;
            }
        }

        sweep ??= _sweeps.GetOrAdd(host, CreateSweep).Value;

        return await sweep.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按 key + value 精确移除。并发下另一个调用可能刚把「新任务」放进字典，
    /// 此时 <c>TryRemove(host)</c> 会把新任务删掉，导致短时间内重复启动扫描。
    /// </summary>
    private void RemoveSweep(string host, Lazy<Task<IReadOnlyList<IPAddress>>> tracked)
    {
        var collection = (ICollection<KeyValuePair<string, Lazy<Task<IReadOnlyList<IPAddress>>>>>)_sweeps;
        collection.Remove(new KeyValuePair<string, Lazy<Task<IReadOnlyList<IPAddress>>>>(host, tracked));
    }

    /// <summary>
    /// 用 <see cref="Lazy{T}"/> 包一层再放进字典：<c>GetOrAdd</c> 的工厂在高并发下可能被调用多次
    /// （只有一份会被保留），如果工厂本身带「启动扫描」的副作用，那些被丢弃的调用就会白扫一遍。
    /// </summary>
    private Lazy<Task<IReadOnlyList<IPAddress>>> CreateSweep(string host) =>
        new(() => StartSweep(host), LazyThreadSafetyMode.ExecutionAndPublication);

    private Task<IReadOnlyList<IPAddress>> StartSweep(string host)
    {
        var firstBatch = new TaskCompletionSource<IReadOnlyList<IPAddress>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var generation = Volatile.Read(ref _generation);

        // 直接启动异步方法：首个 await 前只有建 socket 这类极短的同步工作。
        // 旧实现用 Task.Run 包一层，调用方本身在池线程时还要再排一次队，白白推迟首批结果。
        _ = SweepAsync(host, firstBatch, generation);

        return firstBatch.Task;
    }

    private async Task SweepAsync(
        string host,
        TaskCompletionSource<IReadOnlyList<IPAddress>> firstBatch,
        int generation)
    {
        var pool = _pool;
        if (pool.Count == 0)
        {
            _log.Debug($"加速池为空，{host} 直接走系统 DNS");
            firstBatch.TrySetResult(Array.Empty<IPAddress>());
            return;
        }

        var found = new List<IPAddress>(16);
        var seen = new HashSet<IPAddress>();
        var published = 0;

        var window = Math.Max(0, _firstBatchMs);

        // window == 0 表示「不聚合」：拿到第一个答案就返回，但也不能在还没有答案时抢先发布空结果。
        var minIps = window <= 0 ? 1 : Math.Max(1, _firstBatchMinIps);

        // 发布只由两件事触发：任一查询完成（凑够 minIps）、或独立截止计时器到点。
        void TryPublish(bool force)
        {
            if (Volatile.Read(ref published) != 0)
            {
                return;
            }

            IPAddress[] snapshot;

            lock (found)
            {
                if (!force && found.Count < minIps)
                {
                    return;
                }

                snapshot = found.ToArray();
            }

            if (Interlocked.Exchange(ref published, 1) == 0)
            {
                firstBatch.TrySetResult(snapshot);
            }
        }

        using var deadline = new CancellationTokenSource();
        var deadlineTask = window > 0
            ? PublishOnDeadlineAsync(window, () => TryPublish(force: true), deadline.Token)
            : Task.CompletedTask;

        using var guard = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(4000, _timeoutMs * 8)));

        try
        {
            var ordered = OrderByHealth(pool);

            // 首批只问「延迟低、近期成功率高」的前若干台，其余留给后台。
            var headCount = Math.Min(ordered.Count, Math.Clamp(Math.Max(minIps * 2, 8), 8, FirstBatchServerCap));

            var head = new Task[headCount];
            for (var i = 0; i < headCount; i++)
            {
                head[i] = QueryOneAsync(
                    ordered[i],
                    host,
                    found,
                    seen,
                    generation,
                    () => TryPublish(force: false),
                    guard.Token);
            }

            await Task.WhenAll(head).ConfigureAwait(false);

            // 这里刻意<b>不</b>急着发布空结果：首批没答案不代表池子没答案，
            // 后面还有服务器没问。收口交给截止计时器（窗口）或整轮扫描结束，
            // 否则会把「后面几台能答」的池子直接判死，白白回落系统 DNS。
            if (headCount < ordered.Count)
            {
                await DrainAsync(ordered, headCount, host, found, seen, generation, guard.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"解析 {host} 失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryPublish(force: true);

            try
            {
                deadline.Cancel();
                await deadlineTask.ConfigureAwait(false);
            }
            catch
            {
                // 截止计时器随扫描结束而取消，属正常路径
            }
        }
    }

    /// <summary>
    /// 后台补齐：固定数量 worker 从剩余服务器里取活，只写缓存与候选表，不影响任何等待者。
    /// </summary>
    private async Task DrainAsync(
        IReadOnlyList<DnsPoolEntry> ordered,
        int startIndex,
        string host,
        List<IPAddress> found,
        HashSet<IPAddress> seen,
        int generation,
        CancellationToken token)
    {
        var workers = Math.Min(Math.Max(1, _concurrency), ordered.Count - startIndex);

        // 用单元素数组做游标：局部变量在 async 里被提升为状态机字段，ref 取址容易踩编译器边角
        var cursor = new[] { startIndex - 1 };

        async Task WorkerAsync()
        {
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var index = Interlocked.Increment(ref cursor[0]);
                if (index >= ordered.Count)
                {
                    return;
                }

                if (generation != Volatile.Read(ref _generation))
                {
                    // 池已换过，本轮结果无意义
                    return;
                }

                if (CountFound(found) >= BackgroundTargetIps)
                {
                    return;
                }

                await QueryOneAsync(ordered[index], host, found, seen, generation, null, token).ConfigureAwait(false);
            }
        }

        var tasks = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            tasks[i] = WorkerAsync();
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task PublishOnDeadlineAsync(int windowMs, Action publish, CancellationToken token)
    {
        try
        {
            await Task.Delay(windowMs, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        publish();
    }

    private async Task QueryOneAsync(
        DnsPoolEntry entry,
        string host,
        List<IPAddress> found,
        HashSet<IPAddress> seen,
        int generation,
        Action? onResult,
        CancellationToken token)
    {
        if (!IPAddress.TryParse(entry.Ip, out var server))
        {
            return;
        }

        DnsQueryResult?[] answers;

        try
        {
            // A 与 AAAA 同时问：纯 IPv6 环境下只查 A 会得到空答案，候选池直接少一半路子。
            var queries = new[]
            {
                _query(server, host, _timeoutMs, token, false),
                _query(server, host, _timeoutMs, token, true)
            };

            answers = await Task.WhenAll(queries).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 后台扫描被守卫超时取消：不是这台 DNS 的错，不计入健康度
            return;
        }
        catch (Exception ex)
        {
            RecordHealth(entry.Ip, false);
            _log.Debug($"{entry.Ip} 解析 {host} 失败：{ex.GetType().Name}");
            return;
        }

        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        var addresses = new List<IPAddress>(8);
        var ttl = _maxTtl;
        var answered = false;
        var rejected = 0;

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

            answered = true;

            // 一次合并内的 TTL 取最小值：宁可早一点重新解析，也不要把快过期的地址留太久
            ttl = Math.Min(ttl, Math.Clamp(result.TtlSeconds, _minTtl, _maxTtl));

            foreach (var address in result.Addresses)
            {
                // 投毒/污染的应答必须在入池前丢掉，而不是留到竞速阶段靠失败来淘汰：
                // 它们「失败得很快」反而会占满一批竞速名额，并让 1500ms 的首连预算
                // 全部消耗在必然失败的地址上。
                if (!AddressPolicy.IsRoutable(address))
                {
                    rejected++;
                    continue;
                }

                addresses.Add(address);
            }
        }

        if (addresses.Count == 0)
        {
            // 「答了但全被过滤」比「没答」更该降权：这说明该 DNS 正在返回不可路由的地址，
            // 下次不该再让它进首批。
            RecordHealth(entry.Ip, false);

            if (answered && rejected > 0)
            {
                _log.Debug($"{entry.Ip} 对 {host} 的应答全部不可路由（{rejected} 个），已丢弃");
            }

            return;
        }

        RecordHealth(entry.Ip, true);

        _cache.Merge(host, addresses, ttl, _preferIpv4);

        // 记录「谁给出了这个地址」，供后续竞速失败时归因。同一地址以首个给出者为准：
        // 后给出同一答案的 DNS 只是随声附和，不该分享责任。
        if (_ipSource.Count >= IpSourceSoftLimit)
        {
            _ipSource.Clear();
        }

        foreach (var address in addresses)
        {
            _ipSource.TryAdd(address, entry.Ip);
        }

        var added = false;

        lock (found)
        {
            foreach (var address in addresses)
            {
                if (seen.Add(address))
                {
                    found.Add(address);
                    added = true;
                }
            }

            if (added)
            {
                found.Sort(CompareAddress);
            }
        }

        if (added)
        {
            onResult?.Invoke();
        }
    }

    /// <summary>
    /// 首批排序：失败多于成功的排后面，其余按扫描时的延迟升序。
    /// 不直接按「失败次数」排 —— 那个计数器是累加的，一次历史失败会让一台好 DNS 永远靠后。
    /// </summary>
    /// <remarks>
    /// 这里刻意给「答错」加了双倍权重。实测池内延迟最低的几台恰好是国内公共 DNS，
    /// 它们对 github.com 的应答高度趋同（都返回同一个地址），一旦这个地址不可达，
    /// 「按延迟排序」就会让首批永远押在同一个地址上 —— 单点赌注。
    /// 把答错的台数按双倍计入降级判定，可以让首批逐渐换到「答得出且答得对」的台上去。
    /// </remarks>
    private List<DnsPoolEntry> OrderByHealth(IReadOnlyList<DnsPoolEntry> pool)
    {
        if (pool.Count <= 1)
        {
            return new List<DnsPoolEntry>(pool);
        }

        var scored = new List<(DnsPoolEntry Entry, int Index, int Degraded, int Latency)>(pool.Count);

        for (var i = 0; i < pool.Count; i++)
        {
            var entry = pool[i];
            var degraded = 0;

            if (_serverHealth.TryGetValue(entry.Ip, out var health))
            {
                var ok = Volatile.Read(ref health.Ok);
                var fail = Volatile.Read(ref health.Fail);
                var bad = Volatile.Read(ref health.Bad);

                degraded = (bad * 2) + fail > ok ? 1 : 0;
            }

            scored.Add((entry, i, degraded, entry.LatencyMs));
        }

        scored.Sort((left, right) =>
        {
            if (left.Degraded != right.Degraded)
            {
                return left.Degraded.CompareTo(right.Degraded);
            }

            if (left.Latency != right.Latency)
            {
                return left.Latency.CompareTo(right.Latency);
            }

            return left.Index.CompareTo(right.Index);
        });

        var ordered = new List<DnsPoolEntry>(pool.Count);
        foreach (var item in scored)
        {
            ordered.Add(item.Entry);
        }

        return ordered;
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

    private static int CountFound(List<IPAddress> found)
    {
        lock (found)
        {
            return found.Count;
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
