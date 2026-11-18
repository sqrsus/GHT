using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace GHT.Services;

/// <summary>竞速胜出者：赢的那个 IP 与已建好的连接（调用方负责释放客户端）。</summary>
public readonly record struct RaceWin(IPAddress Ip, TcpClient Client);

/// <summary>
/// 建连失败的类型。分类的意义在于<b>证据强度</b>不同，回灌给 DNS 健康度时权重也不同：
/// <list type="bullet">
/// <item><see cref="Refused"/>：对方回了 RST —— 地址是活的，只是不为这个域名服务。
///       这是「答案本身有问题」的强证据（投毒、过时记录、错误的边缘节点）。</item>
/// <item><see cref="Timeout"/>：连 SYN 都没回应 —— 可能是地址不可达，也可能是本机链路抖动，
///       弱证据，不能据此把一台好 DNS 判死。</item>
/// <item><see cref="Unreachable"/>：明确的路由不可达（无路由、地址族不支持），
///       多半是本机网络环境问题，同样按弱证据处理。</item>
/// </list>
/// </summary>
public enum AddressFailureKind
{
    Refused,
    Timeout,
    Unreachable,
    Other
}

/// <summary>
/// TCP 建连竞速：同时向多个候选 IP 发起 connect，第一个成功的即为上游。
/// 只做握手竞速，不读不写任何业务数据，因此对 TLS 完全透明。
///
/// 句柄纪律：落败的连接必须在返回前被确定性回收。旧实现依赖 ContinueWith 猜任务状态，
/// 在「取消竞速的同时恰好有别的连接完成」这种时序下会漏掉一个已建立的 socket。
///
/// 地址健康度：每个地址记录失败次数与建连耗时 EWMA。近期失败过的地址排到候选队尾（不是直接剔除，
/// 否则池里全是坏 IP 时会直接连不上），已知慢的地址也排后 —— 这些信息本来就是竞速顺手产生的。
/// </summary>
public sealed class UpstreamRacer
{
    /// <summary>等待落败任务自我清理的上限。取消后它们通常立刻结束。</summary>
    /// <remarks>
    /// 从 2 秒收到 1 秒：竞速胜出后其余尝试已经拿到取消信号，正常路径在几十毫秒内就结束了。
    /// 留 2 秒只是让「胜出后还要等 2 秒才返回」变成常态，期间那批任务与它们的 socket 都还在。
    /// 超时后不强行拆任务 —— 它们自己的 catch 分支会 Dispose。
    /// </remarks>
    private static readonly TimeSpan ReapTimeout = TimeSpan.FromSeconds(1);

    /// <summary>失败退避上限：连坏 5 次以上就不再额外惩罚，避免永久拉黑一个其实能用的地址。</summary>
    private const int MaxBackoffSeconds = 60;

    /// <summary>
    /// 健康度条目上限。长期访问大量域名会持续引入新 IP，没有上限时这张表会一路涨。
    /// 4096 对「几百个常用域名 × 每个几十个地址」是足够的，超出的多半是早已不再出现的旧地址。
    /// </summary>
    private const int MaxHealthEntries = 4096;

    /// <summary>超过这个时长没再出现的地址直接淘汰（30 天足够覆盖一次长假）。</summary>
    private static readonly long EvictAfterMs = (long)TimeSpan.FromDays(30).TotalMilliseconds;

    /// <summary>写盘时的条目上限：文件只是「让下次启动少走弯路」的优化，不值得写成几 MB。</summary>
    private const int MaxPersistedEntries = 2048;

    private readonly LogService _log;
    private readonly ConcurrentDictionary<string, AddressHealth> _health = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 健康表条目计数。自己维护是因为 <c>ConcurrentDictionary.Count</c> 要拿全表的锁，
    /// 而「是否超限」这个判断就发生在每次建连尝试的路径上。
    /// </summary>
    private int _healthCount;

    /// <summary>
    /// 建连动作。生产环境就是真 socket；测试可注入确定性实现。
    ///
    /// 为什么要留这个缝：这几个用例要断言的是「失败被记到了哪个地址、耗时多长、取消不该算失败」，
    /// 都属于本类自己的逻辑。如果它们依赖真实 TCP 栈，就会被操作系统的报文行为绑架 ——
    /// 实测 Windows 上连回环上的关闭端口要 <b>2043~2073ms</b> 才收到 RST（正常应低于 1ms），
    /// 于是 1000ms 的竞速预算先取消，用例断言的「失败」实际变成了「没来得及试」，
    /// 本地 Linux 通过、CI 的 Windows 失败。
    /// </summary>
    private readonly Func<IPAddress, int, CancellationToken, Task<TcpClient>> _connect;

    public UpstreamRacer(LogService log)
        : this(log, connect: null)
    {
    }

    internal UpstreamRacer(LogService log, Func<IPAddress, int, CancellationToken, Task<TcpClient>>? connect)
    {
        _log = log;
        _connect = connect ?? ConnectDirectAsync;
    }

    private sealed class AddressHealth
    {
        public int Failures;

        /// <summary>
        /// 最后一次失败的 Unix 毫秒时间戳。
        /// 这里刻意用绝对时间而不是 <see cref="Environment.TickCount64"/>：后者是进程内的相对计时，
        /// 一旦写入磁盘，下次进程启动时基准就变了，退避窗口会算出荒谬的结果。
        /// </summary>
        public long LastFailureUnixMs;

        /// <summary>建连耗时指数滑动平均；负数表示还没有样本。</summary>
        public double EwmaMs = -1;

        /// <summary>
        /// 最后一次「成功或失败」的 Unix 毫秒时间戳，只用于淘汰长期不再出现的地址。
        /// 与 <see cref="LastFailureUnixMs"/> 分开：后者参与退避计算，不能被成功事件刷新。
        /// </summary>
        public long LastSeenUnixMs;
    }

    /// <summary>健康度条目数（诊断与测试用）。</summary>
    public int HealthCount => Volatile.Read(ref _healthCount);

    public async Task<RaceWin?> RaceAsync(
        IReadOnlyList<IPAddress> candidates,
        int port,
        int connectTimeoutMs,
        int batchBudgetMs,
        CancellationToken cancellationToken,
        Action<IPAddress, long, AddressFailureKind>? onFailure = null)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var ordered = OrderByHealth(candidates);

        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 批级预算只由 <paramref name="batchBudgetMs"/> 决定。
        // 旧实现取 min(单地址超时, 批预算) 作为整批的取消时间，于是「单地址超时」和「批预算到期」
        // 共用一个令牌，永远分不清两者 —— 而这正是「失败明细里看不出是地址坏还是预算小」的根源。
        // 单地址超时现在由每次尝试自己的令牌负责（见 TryConnectAsync）。
        if (batchBudgetMs > 0)
        {
            scope.CancelAfter(batchBudgetMs);
        }

        var pending = new List<Task<RaceWin?>>(ordered.Count);
        foreach (var candidate in ordered)
        {
            pending.Add(TryConnectAsync(candidate, port, connectTimeoutMs, scope.Token, onFailure));
        }

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);

            RaceWin? win = null;
            try
            {
                win = await finished.ConfigureAwait(false);
            }
            catch
            {
                // 单条失败忽略，继续等其他候选
            }

            if (win is not null)
            {
                scope.Cancel();
                await ReapLosersAsync(pending).ConfigureAwait(false);

                _log.Debug($"竞速胜出：{win.Value.Ip}:{port}");
                return win;
            }
        }

        return null;
    }

    /// <summary>按「近期失败优先靠后 → 已知慢的靠后 → 未知的靠前」排序，保持相对顺序稳定。</summary>
    private List<IPAddress> OrderByHealth(IReadOnlyList<IPAddress> candidates)
    {
        if (candidates.Count <= 1)
        {
            return new List<IPAddress>(candidates);
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var scored = new List<(IPAddress Address, int Index, int Blocked, double Ewma)>(candidates.Count);

        for (var i = 0; i < candidates.Count; i++)
        {
            var address = candidates[i];
            var blocked = 0;
            var ewma = -1.0;

            if (_health.TryGetValue(address.ToString(), out var health))
            {
                if (health.Failures > 0)
                {
                    var backoff = Math.Min(MaxBackoffSeconds, 5 * health.Failures);
                    var elapsedSec = Math.Max(0, (nowMs - health.LastFailureUnixMs) / 1000.0);
                    if (elapsedSec < backoff)
                    {
                        blocked = 1;
                    }
                }

                ewma = health.EwmaMs;
            }

            scored.Add((address, i, blocked, ewma));
        }

        scored.Sort((left, right) =>
        {
            if (left.Blocked != right.Blocked)
            {
                return left.Blocked.CompareTo(right.Blocked);
            }

            if (left.Ewma != right.Ewma)
            {
                return left.Ewma.CompareTo(right.Ewma);
            }

            return left.Index.CompareTo(right.Index);
        });

        var ordered = new List<IPAddress>(candidates.Count);
        foreach (var item in scored)
        {
            ordered.Add(item.Address);
        }

        return ordered;
    }

    /// <summary>
    /// 生产环境的建连：裸 TCP 握手，对 TLS 完全透明。
    /// 这里刻意<b>不</b>调整套接字缓冲：竞速时可能有 8 个候选同时建连，
    /// 给每个都设 256KB 等于把内核缓冲成本乘以候选数，而落败者在几十毫秒内就被释放。
    /// 调大缓冲放在胜出之后做（ProxyServer.TuneSocket），只对真正跑流量的那一条生效。
    /// </summary>
    private static async Task<TcpClient> ConnectDirectAsync(IPAddress address, int port, CancellationToken token)
    {
        var client = new TcpClient(address.AddressFamily);

        try
        {
            await client.ConnectAsync(address, port, token).ConfigureAwait(false);
            client.NoDelay = true;
            return client;
        }
        catch
        {
            // 失败路径由建连方自己回收；成功的连接交给调用方或 ReapLosersAsync。
            client.Dispose();
            throw;
        }
    }

    private async Task<RaceWin?> TryConnectAsync(
        IPAddress address,
        int port,
        int connectTimeoutMs,
        CancellationToken token,
        Action<IPAddress, long, AddressFailureKind>? onFailure = null)
    {
        var stopwatch = Stopwatch.StartNew();

        // 单地址超时用<b>独立</b>的令牌：否则「这个地址超时了」和「整批预算到期了」
        // 都表现为同一个 token 被取消，无法区分 —— 前者是地址的问题，后者是我们自己的决定。
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (connectTimeoutMs > 0)
        {
            attempt.CancelAfter(connectTimeoutMs);
        }

        try
        {
            var client = await _connect(address, port, attempt.Token).ConfigureAwait(false);
            stopwatch.Stop();

            RecordSuccess(address, stopwatch.Elapsed.TotalMilliseconds);
            return new RaceWin(address, client);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 被首连预算或上层取消：这是「没来得及试完」，不是「这个地址连不上」。
            // 旧实现把两者混为一谈，于是每次预算耗尽都会给全部候选记一次失败，
            // 健康度被系统性污染，好地址会因为「预算小」而被永久降权。
            stopwatch.Stop();
            return null;
        }
        catch (OperationCanceledException)
        {
            // 只有本地址的超时令牌被触发：地址在期限内没连上，属弱证据
            stopwatch.Stop();
            RecordFailure(address);
            onFailure?.Invoke(address, (long)stopwatch.ElapsedMilliseconds, AddressFailureKind.Timeout);
            return null;
        }
        catch (SocketException ex)
        {
            stopwatch.Stop();
            RecordFailure(address);
            onFailure?.Invoke(address, (long)stopwatch.ElapsedMilliseconds, Classify(ex.SocketErrorCode));
            return null;
        }
        catch
        {
            stopwatch.Stop();
            RecordFailure(address);
            onFailure?.Invoke(address, (long)stopwatch.ElapsedMilliseconds, AddressFailureKind.Other);
            return null;
        }
    }

    /// <summary>把套接字错误映射成证据强度。未列出的错误一律按「其它」= 弱证据处理。</summary>
    internal static AddressFailureKind Classify(SocketError error) => error switch
    {
        SocketError.ConnectionRefused or SocketError.ConnectionReset => AddressFailureKind.Refused,

        SocketError.TimedOut => AddressFailureKind.Timeout,

        SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostDown
            or SocketError.NetworkDown or SocketError.HostNotFound or SocketError.NoData
            or SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported
            or SocketError.TryAgain => AddressFailureKind.Unreachable,

        _ => AddressFailureKind.Other
    };

    private void RecordSuccess(IPAddress address, double milliseconds)
    {
        var health = GetHealth(address.ToString());
        health.Failures = 0;
        health.EwmaMs = health.EwmaMs < 0 ? milliseconds : (health.EwmaMs * 0.7) + (milliseconds * 0.3);
        health.LastSeenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        PruneIfNeeded();
    }

    private void RecordFailure(IPAddress address)
    {
        var health = GetHealth(address.ToString());
        health.Failures++;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Volatile.Write(ref health.LastFailureUnixMs, now);
        health.LastSeenUnixMs = now;

        PruneIfNeeded();
    }

    /// <summary>取（必要时创建）某个地址的健康记录。只有真的插入才计数，避免并发下工厂多跑把计数抬高。</summary>
    private AddressHealth GetHealth(string key)
    {
        if (_health.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var fresh = new AddressHealth();

        if (_health.TryAdd(key, fresh))
        {
            Interlocked.Increment(ref _healthCount);
            return fresh;
        }

        return _health.TryGetValue(key, out existing) ? existing : fresh;
    }

    /// <summary>
    /// 健康表的上限维护。先淘汰 30 天没再出现的地址，再按最久未见淘汰到 3/4 ——
    /// 一次淘汰到 3/4 而不是刚好压线，是为了让这项 O(n) 操作摊薄到多次调用上。
    /// </summary>
    private void PruneIfNeeded()
    {
        if (Volatile.Read(ref _healthCount) <= MaxHealthEntries)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cutoff = now - EvictAfterMs;

        foreach (var pair in _health)
        {
            if (Volatile.Read(ref pair.Value.LastSeenUnixMs) < cutoff)
            {
                RemoveHealth(pair);
            }
        }

        if (Volatile.Read(ref _healthCount) <= MaxHealthEntries)
        {
            return;
        }

        var target = MaxHealthEntries - (MaxHealthEntries / 4);
        var overflow = Volatile.Read(ref _healthCount) - target;

        var stale = _health
            .Select(pair => (Pair: pair, Seen: Volatile.Read(ref pair.Value.LastSeenUnixMs)))
            .OrderBy(item => item.Seen)
            .Take(overflow)
            .ToArray();

        foreach (var item in stale)
        {
            RemoveHealth(item.Pair);
        }
    }

    /// <summary>按 key+value 精确移除，避免并发下把同一地址的新计数项误删。</summary>
    private void RemoveHealth(KeyValuePair<string, AddressHealth> pair)
    {
        if (((ICollection<KeyValuePair<string, AddressHealth>>)_health).Remove(pair))
        {
            Interlocked.Decrement(ref _healthCount);
        }
    }

    // ── 健康度持久化 ────────────────────────────────────────────────────

    /// <summary>
    /// 从磁盘载入健康度。失败一律忽略 —— 这份数据只是「让下一次启动少走弯路」的优化，
    /// 读不到就等于全新启动，不能因此影响代理可用性。
    /// </summary>
    /// <param name="path">测试用；生产环境传 null 即用 <see cref="AppPaths.HealthFile"/>。</param>
    public void Load(string? path = null)
    {
        try
        {
            path ??= AppPaths.HealthFile;
            if (!File.Exists(path))
            {
                return;
            }

            using var stream = File.OpenRead(path);
            var dto = JsonSerializer.Deserialize<HealthFileDto>(stream);
            if (dto?.Addresses is null)
            {
                return;
            }

            var loaded = 0;

            // 文件可能来自很老的版本、也可能被手工改大：按「最近出现」排序后只取前 N 条。
            // 这份数据只是启动优化，丢掉一部分不影响可用性，但不能让它把内存顶起来。
            var candidates = dto.Addresses
                .Where(pair => pair.Value is not null && !string.IsNullOrWhiteSpace(pair.Key))
                .OrderByDescending(pair => pair.Value!.LastSeenMs)
                .Take(MaxHealthEntries);

            foreach (var pair in candidates)
            {
                var lastSeen = pair.Value!.LastSeenMs;

                // 旧版文件没有 LastSeenMs：退回失败时间，再退回「现在」。
                // 直接用 0 的话，下一次保存会把它当成 30 天前的条目整批丢掉。
                if (lastSeen <= 0)
                {
                    lastSeen = pair.Value.LastFailureMs > 0
                        ? pair.Value.LastFailureMs
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }

                var health = new AddressHealth
                {
                    Failures = Math.Clamp(pair.Value.Failures, 0, 100),
                    LastFailureUnixMs = pair.Value.LastFailureMs,
                    EwmaMs = pair.Value.EwmaMs,
                    LastSeenUnixMs = lastSeen
                };

                if (_health.TryAdd(pair.Key, health))
                {
                    Interlocked.Increment(ref _healthCount);
                }
                else
                {
                    _health[pair.Key] = health;
                }

                loaded++;
            }

            if (loaded > 0)
            {
                _log.Debug($"已载入地址健康度：{loaded} 条（下次启动不必重新试错）");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"载入地址健康度失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把健康度写盘。用 tmp + 覆盖移动，避免进程在中途被杀时留下半个文件。
    ///
    /// 写盘前先做两件事：丢掉「没有任何样本」与「30 天没再出现」的条目，再按最近出现取前
    /// <see cref="MaxPersistedEntries"/> 条。整份数据只是启动优化，不值得写成几 MB；
    /// 序列化直接写到流上，也不再先拼一个完整字符串（那份字符串在大表下本身就是一次 GC 峰值）。
    /// </summary>
    /// <param name="path">测试用；生产环境传 null 即用 <see cref="AppPaths.HealthFile"/>。</param>
    public void Save(string? path = null)
    {
        try
        {
            path ??= AppPaths.HealthFile;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var cutoff = now - EvictAfterMs;

            var entries = new List<(string Ip, AddressHealth Health)>(Math.Min(_health.Count, MaxPersistedEntries));

            foreach (var pair in _health)
            {
                var health = pair.Value;

                // 没有任何样本的地址不必写：没有信息就没有价值，只会让文件变大。
                if (health.Failures <= 0 && health.EwmaMs < 0)
                {
                    continue;
                }

                if (Volatile.Read(ref health.LastSeenUnixMs) < cutoff)
                {
                    continue;
                }

                entries.Add((pair.Key, health));
            }

            var dto = new HealthFileDto
            {
                Version = 2,
                SavedAt = now,
                Addresses = new Dictionary<string, HealthEntryDto>(
                    Math.Min(entries.Count, MaxPersistedEntries),
                    StringComparer.OrdinalIgnoreCase)
            };

            foreach (var (ip, health) in entries
                         .OrderByDescending(item => Volatile.Read(ref item.Health.LastSeenUnixMs))
                         .Take(MaxPersistedEntries))
            {
                dto.Addresses[ip] = new HealthEntryDto
                {
                    Failures = health.Failures,
                    LastFailureMs = Volatile.Read(ref health.LastFailureUnixMs),
                    EwmaMs = health.EwmaMs,
                    LastSeenMs = Volatile.Read(ref health.LastSeenUnixMs)
                };
            }

            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var temp = path + ".tmp";

            using (var stream = new FileStream(
                       temp,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, dto);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"保存地址健康度失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class HealthFileDto
    {
        public int Version { get; set; }

        public long SavedAt { get; set; }

        public Dictionary<string, HealthEntryDto>? Addresses { get; set; }
    }

    private sealed class HealthEntryDto
    {
        public int Failures { get; set; }

        public long LastFailureMs { get; set; }

        public double EwmaMs { get; set; } = -1;

        /// <summary>最后一次成功或失败的时间；旧版文件里没有这个字段，读出来是 0（按最老处理）。</summary>
        public long LastSeenMs { get; set; }
    }

    /// <summary>
    /// 回收落败者：取消已发生，先给它们一点时间自然结束，再把「已经成功但迟到的连接」释放掉。
    /// 未完成的任务不必强拆——它们的 catch 分支会自行 Dispose。
    /// </summary>
    private static async Task ReapLosersAsync(IReadOnlyList<Task<RaceWin?>> losers)
    {
        if (losers.Count == 0)
        {
            return;
        }

        var all = Task.WhenAll(losers);
        await Task.WhenAny(all, Task.Delay(ReapTimeout)).ConfigureAwait(false);

        foreach (var task in losers)
        {
            if (task.Status != TaskStatus.RanToCompletion || task.Result is not { } win)
            {
                continue;
            }

            try
            {
                win.Client.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}
