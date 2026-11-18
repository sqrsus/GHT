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
/// 5) 一次扫描的所有可变状态都装在 <see cref="SweepContext"/> 里，发布与缓存合并在同一把锁内，
///    换池 / 首批发布 / 截止计时器同时触发时不会再出现「缓存有、候选表没有」的中间态。
/// </summary>
public sealed class ResolverHub
{
    /// <summary>首批最多问多少台 DNS。收益来自「尽快拿到几个可用 IP」，不是把整池问完。</summary>
    private const int FirstBatchServerCap = 32;

    /// <summary>后台补齐的目标候选数。到量即停，避免为一个域名把上千台 DNS 全问一遍。</summary>
    private const int BackgroundTargetIps = 16;

    /// <summary>映射表的软上限。地址集合本身有限，这里只是防止长期运行后无限增长。</summary>
    private const int IpSourceSoftLimit = 8192;

    /// <summary>
    /// 在飞扫描表的硬上限。正常情况下「扫描完成即移除」已经把它压到与并发域名同阶，
    /// 这个阈值只是兜底：万一某条路径漏了移除，也不会一路涨到进程结束。
    /// </summary>
    private const int MaxTrackedSweeps = 2048;

    /// <summary>单台 DNS 的健康记录上限。池子通常只有几十台，这个阈值只在换池很多次后才可能碰到。</summary>
    private const int MaxServerHealth = 2048;

    /// <summary>健康记录超过这个时长没有任何活动就被丢弃（换池后旧服务器会自然被淘汰）。</summary>
    private static readonly long ServerHealthIdleMs = (long)TimeSpan.FromHours(1).TotalMilliseconds;

    // ── 健康度权重与半衰期 ──────────────────────────────────────────────
    // 三类负面信号的证据强度完全不同，混成一个计数器就是把「网线被拔」和「DNS 投毒」当成一回事：
    //   投毒（应答全是不可路由地址）：最强的证据，权重 2，半衰期 30 分钟；
    //   RST（地址可达但拒绝服务）  ：较强，权重 1.5，半衰期 30 分钟；
    //   超时 / 不可达             ：最弱，很可能只是本机链路抖动，权重 1，半衰期 10 分钟。
    private const double PoisonWeight = 2.0;
    private const double RefusedWeight = 1.5;
    private const double SoftWeight = 1.0;

    private static readonly double PoisonHalfLifeMs = TimeSpan.FromMinutes(30).TotalMilliseconds;
    private static readonly double RefusedHalfLifeMs = TimeSpan.FromMinutes(30).TotalMilliseconds;
    private static readonly double SoftHalfLifeMs = TimeSpan.FromMinutes(10).TotalMilliseconds;
    private static readonly double OkHalfLifeMs = TimeSpan.FromMinutes(60).TotalMilliseconds;

    /// <summary>
    /// 降级门槛：加权负面分要超过「1 + 正向分」才降级。
    /// 那个 1.0 是刻意留的余量 —— 单次超时不该让一台好 DNS 立刻掉到队尾。
    /// </summary>
    private const double DegradeFloor = 1.0;

    private readonly LogService _log;
    private readonly DnsQueryHandler _query;
    private readonly DnsResultCache _cache = new();

    /// <summary>
    /// 在飞的扫描，键是 <c>host#generation</c>。
    /// 带上版本号是为了让「换池前发起、换池后才发现」的扫描无法被新请求复用。
    ///
    /// 生命周期：扫描一结束就把自己这一项移除（见 <see cref="RemoveWhenDoneAsync"/>）。
    /// 旧实现只在「下一次解析同一个域名」时才顺手清理，于是高频访问大量不同域名时，
    /// 这张表只增不减 —— 每项都攥着 Task、候选表与首批快照，长跑下来是明确的内存泄漏。
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<SweepEntry>> _sweeps =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 一次扫描的两个句柄。<see cref="First"/> 给调用方等待，<see cref="Completion"/> 给回收逻辑用：
    /// 首批发布之后后台补齐还会继续跑，只在首批完成时回收会掐掉缓存补充。
    /// </summary>
    private sealed class SweepEntry
    {
        public SweepEntry(Task<IReadOnlyList<IPAddress>> first, Task completion)
        {
            First = first;
            Completion = completion;
        }

        public Task<IReadOnlyList<IPAddress>> First { get; }

        public Task Completion { get; }
    }

    /// <summary>
    /// 每台公共 DNS 在运行期的命中/失败计数。UI 用它在加速池里显示「健康 / 降级 / 失效」，
    /// 否则用户只能看到一个扫描时的静态延迟，无法判断这台 DNS 现在还灵不灵。
    /// 首批也会用它把「失败多于成功」的服务器排到后面。
    /// </summary>
    private readonly ConcurrentDictionary<string, ServerHealth> _serverHealth =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>健康表的条目计数。维护它的理由见 <see cref="ServerHealthCount"/>。</summary>
    private int _serverHealthCount;

    /// <summary>
    /// 候选 IP → 首个给出它的那台 DNS。
    ///
    /// 为什么需要这张表：健康度原本只区分「能答 / 不能答」，但真正有害的是<b>能答却答错</b> ——
    /// 实测池里有 DNS 对 github.com 返回 127.0.0.1、198.51.44.8 这类地址，它们「答得很积极」，
    /// 于是永远排在首批，而答案永远连不上。有了 IP 到来源的映射，竞速失败时才能把责任
    /// 精确记到那台 DNS 上，而不是笼统地让所有 DNS 一起背锅。
    ///
    /// 上限用「先进先出淘汰」而不是 <c>Clear()</c>：整表清空会让紧随其后的失败归因全部丢失
    /// （那几秒里所有 DNS 都突然「没有责任」），分批淘汰只损失最早的一批。
    /// </summary>
    private readonly ConcurrentDictionary<IPAddress, string> _ipSource = new();

    /// <summary>插入顺序，用于超限时的先进先出淘汰。</summary>
    private readonly ConcurrentQueue<IPAddress> _ipSourceOrder = new();

    /// <summary>插入计数（近似值，只用于判断「该不该淘汰」，不要求与字典严格一致）。</summary>
    private int _ipSourceCount;

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

    /// <summary>扫描表当前条目数（诊断用）。正常情况下应随时回到 0 附近。</summary>
    public int TrackedSweepCount => _sweeps.Count;

    /// <summary>DNS 健康记录条目数（诊断用）。用自维护计数：<c>Count</c> 要拿全表的锁，热路径上代价太高。</summary>
    public int ServerHealthCount => Volatile.Read(ref _serverHealthCount);

    /// <summary>地址到来源的映射条目数（诊断用）。</summary>
    public int IpSourceCount => Volatile.Read(ref _ipSourceCount);

    /// <summary>取某台公共 DNS 的成功/失败次数（从未参与解析时返回 (0,0)）。</summary>
    /// <remarks>「答了但答案连不上」也计入失败，因为对使用者而言它与解析失败没有区别。</remarks>
    public (int Ok, int Fail) GetServerHealth(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !_serverHealth.TryGetValue(ip, out var health))
        {
            return (0, 0);
        }

        lock (health)
        {
            return (health.OkRaw, health.BadRaw + health.RefusedRaw + health.FailRaw);
        }
    }

    /// <summary>
    /// 竞速判定某个候选地址连不上时回灌：把责任记到「给出这个答案的那台 DNS」上。
    ///
    /// 只影响排序，不影响可用性 —— 被记过的 DNS 仍会参与解析，只是排到后面。
    /// 这是刻意的：池里如果全是劣质 DNS，直接剔除会让人彻底连不上，降权则只是「先用好的」。
    ///
    /// <paramref name="kind"/> 决定这条证据的分量：RST 说明「地址是真的、但不管这个域名」，
    /// 基本可断定答案有问题；超时则可能只是本机链路抖动，权重低且衰减快。
    /// 把两者混为一谈，会让一次网络抖动把一批好 DNS 永久降权。
    /// </summary>
    public void ReportBadAnswer(IPAddress? address, AddressFailureKind kind)
    {
        if (address is null)
        {
            return;
        }

        if (!_ipSource.TryGetValue(address, out var server) || string.IsNullOrEmpty(server))
        {
            return;
        }

        var health = GetHealth(server);
        var now = Environment.TickCount64;

        lock (health)
        {
            health.LastTouchMs = now;

            switch (kind)
            {
                case AddressFailureKind.Refused:
                    health.RefusedRaw++;
                    health.RefusedScore = Decay(health.RefusedScore, health.RefusedStamp, now, RefusedHalfLifeMs) + 1;
                    health.RefusedStamp = now;
                    break;

                case AddressFailureKind.Timeout:
                case AddressFailureKind.Unreachable:
                case AddressFailureKind.Other:
                default:
                    health.FailRaw++;
                    health.FailScore = Decay(health.FailScore, health.FailStamp, now, SoftHalfLifeMs) + 1;
                    health.FailStamp = now;
                    break;
            }
        }
    }

    /// <summary>
    /// 单台 DNS 的健康记录。
    ///
    /// 原始计数只增不减，只用于 UI 展示（用户看到的是「这台答错过几次」的事实）；
    /// 选台排序用的是<b>带时间衰减的分数</b>，因此一次历史失败会随好表现逐渐被冲淡，
    /// 不会让一台 DNS 因为开机时的一次抖动永久排在队尾。
    /// </summary>
    private sealed class ServerHealth
    {
        public int OkRaw;
        public int BadRaw;
        public int RefusedRaw;
        public int FailRaw;

        public double OkScore;
        public long OkStamp;

        public double BadScore;
        public long BadStamp;

        public double RefusedScore;
        public long RefusedStamp;

        public double FailScore;
        public long FailStamp;

        /// <summary>
        /// 最后一次被更新的时刻（<see cref="Environment.TickCount64"/>）。
        /// 只用于淘汰「换池后不再出现的旧服务器」，不参与任何排序：
        /// 它与各分桶的 stamp 是两个概念，混用会让衰减算错。
        /// </summary>
        public long LastTouchMs;

        /// <summary>加权负面分（已衰减）。</summary>
        public double NegativeScore(long now) =>
            (Decay(BadScore, BadStamp, now, PoisonHalfLifeMs) * PoisonWeight)
            + (Decay(RefusedScore, RefusedStamp, now, RefusedHalfLifeMs) * RefusedWeight)
            + (Decay(FailScore, FailStamp, now, SoftHalfLifeMs) * SoftWeight);

        public double PositiveScore(long now) => Decay(OkScore, OkStamp, now, OkHalfLifeMs);
    }

    /// <summary>指数衰减：半衰期内值减半。时钟回拨时保持原值，不清零。</summary>
    private static double Decay(double value, long stampMs, long nowMs, double halfLifeMs)
    {
        if (value <= 0)
        {
            return 0;
        }

        var elapsed = nowMs - stampMs;
        if (elapsed <= 0 || halfLifeMs <= 0)
        {
            return value;
        }

        return value * Math.Pow(0.5, elapsed / halfLifeMs);
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

        // 在飞的扫描全部属于旧版本，留着只会让新请求复用一个注定丢弃结果的扫描。
        _sweeps.Clear();

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

        var generation = Volatile.Read(ref _generation);
        var key = host + "#" + generation.ToString();

        var lazy = _sweeps.GetOrAdd(key, _ => CreateSweep(host, generation, key));
        var entry = lazy.Value;

        // 扫描可能在工厂返回之前就结束了（池子为空时是同步收口的）。
        // 那种情况下字典里留下的是一个已完成的项，这里顺手回收，不必等下一次同域名请求。
        if (entry.Completion.IsCompleted)
        {
            RemoveSweep(key, lazy);
        }
        else if (_sweeps.Count > MaxTrackedSweeps)
        {
            PruneCompletedSweeps();
        }

        return await entry.First.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按 key + value 精确移除。并发下另一个调用可能刚把「新任务」放进字典，
    /// 此时 <c>TryRemove(key)</c> 会把新任务删掉，导致短时间内重复启动扫描。
    /// </summary>
    private void RemoveSweep(string key, Lazy<SweepEntry> tracked)
    {
        var collection = (ICollection<KeyValuePair<string, Lazy<SweepEntry>>>)_sweeps;
        collection.Remove(new KeyValuePair<string, Lazy<SweepEntry>>(key, tracked));
    }

    /// <summary>兜底清理：把已完成但没被移除的项扫出去。只在超过阈值时调用，避免变成每次解析的开销。</summary>
    private void PruneCompletedSweeps()
    {
        foreach (var pair in _sweeps)
        {
            if (pair.Value.IsValueCreated && pair.Value.Value.Completion.IsCompleted)
            {
                RemoveSweep(pair.Key, pair.Value);
            }
        }
    }

    /// <summary>
    /// 用 <see cref="Lazy{T}"/> 包一层再放进字典：<c>GetOrAdd</c> 的工厂在高并发下可能被调用多次
    /// （只有一份会被保留），如果工厂本身带「启动扫描」的副作用，那些被丢弃的调用就会白扫一遍。
    /// </summary>
    private Lazy<SweepEntry> CreateSweep(string host, int generation, string key) =>
        new(() => StartSweep(host, generation, key), LazyThreadSafetyMode.ExecutionAndPublication);

    private SweepEntry StartSweep(string host, int generation, string key)
    {
        var window = Math.Max(0, _firstBatchMs);

        // window == 0 表示「不聚合」：拿到第一个答案就返回，但也不能在还没有答案时抢先发布空结果。
        var minIps = window <= 0 ? 1 : Math.Max(1, _firstBatchMinIps);

        var context = new SweepContext(this, host, generation, minIps);

        // 直接启动异步方法：首个 await 前只有建 socket 这类极短的同步工作。
        // 旧实现用 Task.Run 包一层，调用方本身在池线程时还要再排一次队，白白推迟首批结果。
        var completion = SweepAsync(context, window);

        var entry = new SweepEntry(context.FirstBatch, completion);
        _ = RemoveWhenDoneAsync(key, entry, completion);

        return entry;
    }

    /// <summary>
    /// 扫描结束即把自己这一项从字典里摘掉。
    ///
    /// 这里先 <c>Task.Yield()</c> 让出一次：扫描可能在 <c>GetOrAdd</c> 的工厂返回之前就同步结束，
    /// 那种情况下字典里还没有这一项，过早移除会漏掉它（剩下的交给下一次同域名解析与阈值兜底）。
    /// </summary>
    private async Task RemoveWhenDoneAsync(string key, SweepEntry entry, Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            // 扫描自身的异常已经在 SweepAsync 里记录过，这里只负责回收
        }

        await Task.Yield();

        if (_sweeps.TryGetValue(key, out var tracked) && ReferenceEquals(tracked.Value, entry))
        {
            RemoveSweep(key, tracked);
        }
    }

    private async Task SweepAsync(SweepContext context, int window)
    {
        var pool = _pool;
        if (pool.Count == 0)
        {
            _log.Debug($"加速池为空，{context.Host} 直接走系统 DNS");
            context.PublishFinal();
            return;
        }

        using var deadline = new CancellationTokenSource();
        var deadlineTask = window > 0
            ? PublishOnDeadlineAsync(window, context.PublishOnWindow, deadline.Token)
            : Task.CompletedTask;

        using var guard = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(4000, _timeoutMs * 8)));

        try
        {
            var ordered = OrderByHealth(pool);
            var minIps = context.MinIps;

            // 首批只问「延迟低、近期成功率高」的前若干台，其余留给后台。
            var headCount = Math.Min(ordered.Count, Math.Clamp(Math.Max(minIps * 2, 8), 8, FirstBatchServerCap));

            var head = new Task[headCount];
            for (var i = 0; i < headCount; i++)
            {
                head[i] = QueryOneAsync(ordered[i], context, guard.Token);
            }

            await Task.WhenAll(head).ConfigureAwait(false);

            // 这里刻意<b>不</b>急着发布空结果：首批没答案不代表池子没答案，
            // 后面还有服务器没问。收口交给截止计时器（窗口）或整轮扫描结束，
            // 否则会把「后面几台能答」的池子直接判死，白白回落系统 DNS。
            if (headCount < ordered.Count)
            {
                await DrainAsync(ordered, headCount, context, guard.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"解析 {context.Host} 失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            context.PublishFinal();

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
        SweepContext context,
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

                if (context.IsStale)
                {
                    // 池已换过，本轮结果无意义
                    return;
                }

                if (context.Count >= BackgroundTargetIps)
                {
                    return;
                }

                await QueryOneAsync(ordered[index], context, token).ConfigureAwait(false);
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

    private async Task QueryOneAsync(DnsPoolEntry entry, SweepContext context, CancellationToken token)
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
                _query(server, context.Host, _timeoutMs, token, false),
                _query(server, context.Host, _timeoutMs, token, true)
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
            RecordFail(entry.Ip);
            _log.Debug($"{entry.Ip} 解析 {context.Host} 失败：{ex.GetType().Name}");
            return;
        }

        if (context.IsStale)
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
                // 它们「失败得很快」反而会占满一批竞速名额，并让首连预算
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
            if (answered && rejected > 0)
            {
                RecordPoison(entry.Ip);
                _log.Debug($"{entry.Ip} 对 {context.Host} 的应答全部不可路由（{rejected} 个），已丢弃");
            }
            else
            {
                RecordFail(entry.Ip);
            }

            return;
        }

        RecordOk(entry.Ip);

        if (context.Accept(entry.Ip, addresses, ttl))
        {
            context.PublishIfReady();
        }
    }

    /// <summary>
    /// 首批排序：加权负面分高的排后面，其余按扫描时的延迟升序。
    ///
    /// 这里刻意区分了证据强度：投毒与 RST 是「答案本身有问题」的强证据，
    /// 超时/不可达是弱证据（可能只是本机链路抖动），三者权重与半衰期都不同。
    /// 全部按时间衰减，因此一次历史失败会随好表现被冲淡 —— 不会让一台 DNS 永久靠后。
    /// </summary>
    private List<DnsPoolEntry> OrderByHealth(IReadOnlyList<DnsPoolEntry> pool)
    {
        if (pool.Count <= 1)
        {
            return new List<DnsPoolEntry>(pool);
        }

        var now = Environment.TickCount64;
        var scored = new List<(DnsPoolEntry Entry, int Index, int Degraded, int Latency)>(pool.Count);

        for (var i = 0; i < pool.Count; i++)
        {
            var entry = pool[i];
            var degraded = 0;

            if (_serverHealth.TryGetValue(entry.Ip, out var health))
            {
                double negative;
                double positive;

                lock (health)
                {
                    negative = health.NegativeScore(now);
                    positive = health.PositiveScore(now);
                }

                degraded = negative > positive + DegradeFloor ? 1 : 0;
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

    /// <summary>答出了可用地址：累加正向分，让历史失败随时间被冲淡。</summary>
    private void RecordOk(string ip)
    {
        var health = GetHealth(ip);
        var now = Environment.TickCount64;

        lock (health)
        {
            health.OkRaw++;
            health.OkScore = Decay(health.OkScore, health.OkStamp, now, OkHalfLifeMs) + 1;
            health.OkStamp = now;
            health.LastTouchMs = now;
        }

        MaybePruneHealth();
    }

    /// <summary>答不出（超时、传输失败、空答案）：弱证据，衰减最快的那个桶。</summary>
    private void RecordFail(string ip)
    {
        var health = GetHealth(ip);
        var now = Environment.TickCount64;

        lock (health)
        {
            health.FailRaw++;
            health.FailScore = Decay(health.FailScore, health.FailStamp, now, SoftHalfLifeMs) + 1;
            health.FailStamp = now;
            health.LastTouchMs = now;
        }

        MaybePruneHealth();
    }

    /// <summary>答了，但答案全是不可路由地址：强证据（投毒/污染）。</summary>
    private void RecordPoison(string ip)
    {
        var health = GetHealth(ip);
        var now = Environment.TickCount64;

        lock (health)
        {
            health.BadRaw++;
            health.BadScore = Decay(health.BadScore, health.BadStamp, now, PoisonHalfLifeMs) + 1;
            health.BadStamp = now;
            health.LastTouchMs = now;
        }

        MaybePruneHealth();
    }

    private ServerHealth GetHealth(string ip)
    {
        if (_serverHealth.TryGetValue(ip, out var existing))
        {
            return existing;
        }

        var fresh = new ServerHealth();

        // TryAdd 而不是 GetOrAdd：只有真的插进去才该计数，
        // 否则并发下工厂多跑几次就会把计数器抬高，上限判断跟着失真。
        if (_serverHealth.TryAdd(ip, fresh))
        {
            Interlocked.Increment(ref _serverHealthCount);
            return fresh;
        }

        return _serverHealth.TryGetValue(ip, out existing) ? existing : fresh;
    }

    /// <summary>
    /// 健康表的上限维护。
    ///
    /// 这张表原本只增不减：换池之后旧服务器永远留在里面，长跑会积累成「几千台 DNS 的计数」。
    /// 淘汰分两步：先丢掉一小时没有任何活动（多半是已经不在池里）的，仍然超限再按最久未活动淘汰到 3/4。
    /// </summary>
    private void MaybePruneHealth()
    {
        if (Volatile.Read(ref _serverHealthCount) <= MaxServerHealth)
        {
            return;
        }

        var now = Environment.TickCount64;
        var idleBefore = now - ServerHealthIdleMs;

        foreach (var pair in _serverHealth)
        {
            long touch;

            lock (pair.Value)
            {
                touch = pair.Value.LastTouchMs;
            }

            if (touch < idleBefore)
            {
                RemoveServerHealth(pair);
            }
        }

        if (Volatile.Read(ref _serverHealthCount) <= MaxServerHealth)
        {
            return;
        }

        var target = MaxServerHealth - (MaxServerHealth / 4);
        var overflow = Volatile.Read(ref _serverHealthCount) - target;

        var stale = _serverHealth
            .Select(pair =>
            {
                lock (pair.Value)
                {
                    return (Pair: pair, Touch: pair.Value.LastTouchMs);
                }
            })
            .OrderBy(item => item.Touch)
            .Take(overflow)
            .ToArray();

        foreach (var item in stale)
        {
            RemoveServerHealth(item.Pair);
        }
    }

    /// <summary>按 key+value 精确移除，避免并发下把同一台服务器的新计数项误删。</summary>
    private void RemoveServerHealth(KeyValuePair<string, ServerHealth> pair)
    {
        if (((ICollection<KeyValuePair<string, ServerHealth>>)_serverHealth).Remove(pair))
        {
            Interlocked.Decrement(ref _serverHealthCount);
        }
    }

    /// <summary>
    /// 记下地址的来源。超过软上限时按插入顺序淘汰四分之一 ——
    /// 不再整表 <c>Clear()</c>：那会让紧随其后的失败归因全部丢失，看起来像「所有 DNS 突然都没责任」。
    /// </summary>
    private void RememberIpSource(IPAddress address, string server)
    {
        if (!_ipSource.TryAdd(address, server))
        {
            return;
        }

        _ipSourceOrder.Enqueue(address);

        if (Interlocked.Increment(ref _ipSourceCount) <= IpSourceSoftLimit)
        {
            return;
        }

        var target = IpSourceSoftLimit - (IpSourceSoftLimit / 4);

        while (Volatile.Read(ref _ipSourceCount) > target && _ipSourceOrder.TryDequeue(out var stale))
        {
            if (_ipSource.TryRemove(stale, out _))
            {
                Interlocked.Decrement(ref _ipSourceCount);
            }
        }
    }

    /// <summary>
    /// 一次扫描的显式上下文。把 host、池版本、候选表、首批发布状态绑在一起，
    /// 而不是散落成闭包捕获的局部变量 —— 后者在「换池 / 发布 / 计时器同时触发」时
    /// 很难说清哪份数据属于哪一轮扫描。
    ///
    /// 发布与缓存合并在<b>同一把锁</b>内完成：拆开的话，首批快照可能拍到
    /// 「缓存已经写进去、候选表还没加进来」的中间态，调用方拿到的列表就少了一批地址。
    /// </summary>
    private sealed class SweepContext
    {
        private readonly object _gate = new();
        private readonly List<IPAddress> _found = new(16);
        private readonly HashSet<IPAddress> _seen = new();
        private readonly TaskCompletionSource<IReadOnlyList<IPAddress>> _firstBatch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly ResolverHub _hub;
        private readonly int _generation;

        private bool _published;

        public SweepContext(ResolverHub hub, string host, int generation, int minIps)
        {
            _hub = hub;
            Host = host;
            _generation = generation;
            MinIps = Math.Max(1, minIps);
        }

        public string Host { get; }

        public int MinIps { get; }

        public Task<IReadOnlyList<IPAddress>> FirstBatch => _firstBatch.Task;

        /// <summary>池在扫描途中被换掉：本轮结果一律丢弃，既不写缓存也不发布候选。</summary>
        public bool IsStale => _generation != Volatile.Read(ref _hub._generation);

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _found.Count;
                }
            }
        }

        /// <summary>收入一台 DNS 的答案。返回是否带来了新地址。</summary>
        public bool Accept(string server, IReadOnlyList<IPAddress> addresses, int ttl)
        {
            lock (_gate)
            {
                if (IsStale)
                {
                    return false;
                }

                _hub._cache.Merge(Host, addresses, ttl, _hub._preferIpv4);

                var added = false;

                foreach (var address in addresses)
                {
                    // 记录「谁给出了这个地址」，供后续竞速失败时归因。同一地址以首个给出者为准：
                    // 后给出同一答案的 DNS 只是随声附和，不该分享责任。
                    _hub.RememberIpSource(address, server);

                    if (_seen.Add(address))
                    {
                        _found.Add(address);
                        added = true;
                    }
                }

                if (added)
                {
                    _found.Sort(CompareAddress);
                }

                return added;
            }
        }

        /// <summary>凑够 <see cref="MinIps"/> 就提前发布（由任一查询完成后调用）。</summary>
        public void PublishIfReady()
        {
            lock (_gate)
            {
                if (_published || _found.Count < MinIps)
                {
                    return;
                }

                PublishLocked();
            }
        }

        /// <summary>
        /// 窗口到点：有多少发多少，<b>包括空列表</b>。
        ///
        /// 为什么允许发空：到点意味着「首批这几台在窗口内什么都没给出」，此时调用方
        /// 更应该立刻回落系统 DNS，而不是继续等整轮扫描 —— 后台扫描仍会补齐缓存，
        /// 下一次请求就能命中。反过来，若在还有查询在飞时硬等，首连预算会被整轮扫描吃光，
        /// 用户看到的是「打不开」而不是「先从系统 DNS 走」。
        /// </summary>
        public void PublishOnWindow()
        {
            lock (_gate)
            {
                if (!_published)
                {
                    PublishLocked();
                }
            }
        }

        /// <summary>整轮结束：这是唯一允许发布空结果的出口。</summary>
        public void PublishFinal()
        {
            lock (_gate)
            {
                if (_published)
                {
                    return;
                }

                PublishLocked();
            }
        }

        /// <summary>调用方必须已持有 <see cref="_gate"/>。</summary>
        private void PublishLocked()
        {
            _published = true;
            _firstBatch.TrySetResult(_found.ToArray());
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
