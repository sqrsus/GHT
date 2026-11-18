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
        var deadlineExpired = 0;

        var window = Math.Max(0, _firstBatchMs);

        // window == 0 表示「不聚合」：拿到第一个答案就返回，但也不能在还没有答案时抢先发布空结果。
        var minIps = window <= 0 ? 1 : Math.Max(1, _firstBatchMinIps);

        // 发布由三件事触发：凑够 minIps、截止计时器到点、整轮扫描结束。
        void TryPublish(bool force, bool allowEmpty = false)
        {
            if (Volatile.Read(ref published) != 0)
            {
                return;
            }

            IPAddress[] snapshot;

            lock (found)
            {
                if (allowEmpty)
                {
                    // 整轮扫描已经结束：有多少发多少，哪怕是空。
                    // 这是唯一允许发布空结果的路径 —— 不收在这里，等待者会永远等下去
                    // （调用方传的是 CancellationToken.None 时就是死锁）。
                    snapshot = found.ToArray();
                }
                else
                {
                    // 两条都不能省，各自挡住一类真实故障：

                    // (1) 到点（force）只是「不再等凑够 minIps」，<b>不是</b>「允许发布空结果」。
                    //     跨境 UDP 到公共 DNS 的 RTT 经常 150~400ms，200ms 窗口到点时一台都没回是常态。
                    //     发布空数组等于当场宣布「池里对这个域名没有答案」，上层于是要么直接回落系统 DNS，
                    //     要么（有静态候选时）拿一批未验证的远端地址把首连预算烧光。
                    //     实测日志里 github.com / githubassets / alive.github.com 连续三次「首连预算用尽」
                    //     全部由此产生，而它们的 DNS 答案在几百毫秒后其实是有的。
                    if (found.Count == 0)
                    {
                        return;
                    }

                    // (2) 窗口到点之后，「凑够 minIps」这个条件必须作废，改成有一个发一个。
                    //     github.com 的 A 记录只有 1 个地址，32 台 DNS 答完去重后往往还是 1~2 个，
                    //     永远凑不到默认的 5 个 —— 首批会一直压到整轮扫描结束，等于没有窗口。
                    var required = force || Volatile.Read(ref deadlineExpired) != 0 ? 1 : minIps;
                    if (found.Count < required)
                    {
                        return;
                    }

                    snapshot = found.ToArray();
                }
            }

            if (Interlocked.Exchange(ref published, 1) == 0)
            {
                firstBatch.TrySetResult(snapshot);
            }
        }

        using var deadline = new CancellationTokenSource();
        var deadlineTask = window > 0
            ? PublishOnDeadlineAsync(
                window,
                () =>
                {
                    Volatile.Write(ref deadlineExpired, 1);
                    TryPublish(force: true);
                },
                deadline.Token)
            : Task.CompletedTask;

        using var guard = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(4000, _timeoutMs * 8)));

        try
        {
            var ordered = OrderByHealth(pool);

            // 首批问多少台：旧值 10 台的假设是「最快的那几台一定答得回来」，但跨境 DNS 的 RTT
            // 分布很散，前 10 台同时碰上抖动的概率并不低。放宽到 16~32 台，
            // 代价是首批多十几个 UDP 包（可忽略），收益是首批空手的概率显著下降。
            var headCount = Math.Min(ordered.Count, Math.Clamp(Math.Max(minIps * 4, 16), 16, FirstBatchServerCap));

            Action onAnswer = () => TryPublish(force: false);

            var head = new Task[headCount];
            for (var i = 0; i < headCount; i++)
            {
                head[i] = QueryOneAsync(ordered[i], host, found, seen, generation, onAnswer, guard.Token);
            }

            // 后台补齐<b>与首批并行</b>，而不是等首批全部结束才开始。
            // 旧实现是 `await Task.WhenAll(head)` 之后才 drain，于是「首批那十几台都慢」
            // 会直接放大成「整轮都慢」：drain 要等最慢的那台超时才开工，首批窗口早就过完了。
            // 现在两边同时跑，谁先摸到答案谁触发发布。drain 也必须带 onAnswer ——
            // 否则它找到的地址不会触发发布，只能等整轮结束。
            var drain = headCount < ordered.Count
                ? DrainAsync(ordered, headCount, host, found, seen, generation, onAnswer, guard.Token)
                : Task.CompletedTask;

            await Task.WhenAll(head.Append(drain)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"解析 {host} 失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 整轮结束还是空的，说明池子确实给不出这个域名的任何可用地址 ——
            // 这是唯一允许发布空结果的地方，上层据此回落系统 DNS。
            TryPublish(force: true, allowEmpty: true);

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
    /// 后台补齐：固定数量 worker 从剩余服务器里取活，写缓存与候选表。
    /// <paramref name="onResult"/> 与首批共用同一个发布回调 —— drain 摸到的地址同样会触发首批发布，
    /// 否则「首批那十几台都慢、后面几台很快」这种情况会一直压到整轮结束才返回。
    /// </summary>
    private async Task DrainAsync(
        IReadOnlyList<DnsPoolEntry> ordered,
        int startIndex,
        string host,
        List<IPAddress> found,
        HashSet<IPAddress> seen,
        int generation,
        Action? onResult,
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

                await QueryOneAsync(ordered[index], host, found, seen, generation, onResult, token).ConfigureAwait(false);
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
        var dropped = 0;

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

            // 入池前就丢掉不可路由地址。投毒/劫持型公共 DNS 会把域名「解析」到 127.0.0.1、
            // 0.0.0.0、::1，这些地址留在池里只会：占掉竞速名额、把每次必败记成地址健康度、
            // 以及让 TLS 探测把并发槽浪费在连本机这种毫无意义的目标上。
            foreach (var address in result.Addresses)
            {
                if (AddressPolicy.IsRoutable(address))
                {
                    addresses.Add(address);
                }
                else
                {
                    dropped++;
                }
            }

            // 一次合并内的 TTL 取最小值：宁可早一点重新解析，也不要把快过期的地址留太久
            ttl = Math.Min(ttl, Math.Clamp(result.TtlSeconds, _minTtl, _maxTtl));
        }

        if (!answered)
        {
            RecordHealth(entry.Ip, false);
            return;
        }

        if (addresses.Count == 0)
        {
            // 有应答，但答案全是不可路由地址 —— 这台 DNS 对这个域名是投毒源。
            // 计入失败，首批排序会把它挪到后面，后续扫描就少踩一次。
            RecordHealth(entry.Ip, false);
            _log.Debug($"{entry.Ip} 对 {host} 返回 {dropped} 个不可路由地址，已全部丢弃");
            return;
        }

        RecordHealth(entry.Ip, true);

        _cache.Merge(host, addresses, ttl, _preferIpv4);

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
                degraded = Volatile.Read(ref health.Fail) > Volatile.Read(ref health.Ok) ? 1 : 0;
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
