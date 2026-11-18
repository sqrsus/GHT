using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace GHT.Services;

/// <summary>竞速胜出者：赢的那个 IP 与已建好的连接（调用方负责释放客户端）。</summary>
public readonly record struct RaceWin(IPAddress Ip, TcpClient Client);

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
    private static readonly TimeSpan ReapTimeout = TimeSpan.FromSeconds(2);

    /// <summary>失败退避上限：连坏 5 次以上就不再额外惩罚，避免永久拉黑一个其实能用的地址。</summary>
    private const int MaxBackoffSeconds = 60;

    private readonly LogService _log;
    private readonly ConcurrentDictionary<string, AddressHealth> _health = new(StringComparer.OrdinalIgnoreCase);

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
    }

    public async Task<RaceWin?> RaceAsync(
        IReadOnlyList<IPAddress> candidates,
        int port,
        int connectTimeoutMs,
        int batchBudgetMs,
        CancellationToken cancellationToken,
        Action<IPAddress, long>? onFailure = null)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var ordered = OrderByHealth(candidates);

        var limits = new List<int>(2);
        if (connectTimeoutMs > 0)
        {
            limits.Add(connectTimeoutMs);
        }

        if (batchBudgetMs > 0)
        {
            limits.Add(batchBudgetMs);
        }

        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (limits.Count > 0)
        {
            scope.CancelAfter(limits.Min());
        }

        var pending = new List<Task<RaceWin?>>(ordered.Count);
        foreach (var candidate in ordered)
        {
            pending.Add(TryConnectAsync(candidate, port, scope.Token, onFailure));
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
        CancellationToken token,
        Action<IPAddress, long>? onFailure = null)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = await _connect(address, port, token).ConfigureAwait(false);
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
        catch
        {
            stopwatch.Stop();
            RecordFailure(address);
            onFailure?.Invoke(address, (long)stopwatch.ElapsedMilliseconds);
            return null;
        }
    }

    private void RecordSuccess(IPAddress address, double milliseconds)
    {
        var health = _health.GetOrAdd(address.ToString(), _ => new AddressHealth());
        health.Failures = 0;
        health.EwmaMs = health.EwmaMs < 0 ? milliseconds : (health.EwmaMs * 0.7) + (milliseconds * 0.3);
    }

    private void RecordFailure(IPAddress address)
    {
        var health = _health.GetOrAdd(address.ToString(), _ => new AddressHealth());
        health.Failures++;
        Volatile.Write(ref health.LastFailureUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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

            foreach (var pair in dto.Addresses)
            {
                if (pair.Value is null || string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                _health[pair.Key] = new AddressHealth
                {
                    Failures = Math.Clamp(pair.Value.Failures, 0, 100),
                    LastFailureUnixMs = pair.Value.LastFailureMs,
                    EwmaMs = pair.Value.EwmaMs
                };

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

    /// <summary>把健康度写盘。用 tmp + Move 覆盖，避免进程在中途被杀时留下半个文件。</summary>
    /// <param name="path">测试用；生产环境传 null 即用 <see cref="AppPaths.HealthFile"/>。</param>
    public void Save(string? path = null)
    {
        try
        {
            path ??= AppPaths.HealthFile;

            var dto = new HealthFileDto
            {
                Version = 1,
                SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Addresses = new Dictionary<string, HealthEntryDto>(_health.Count)
            };

            foreach (var pair in _health)
            {
                var health = pair.Value;

                // 没有任何样本的地址不必写：没有信息就没有价值，只会让文件变大。
                if (health.Failures <= 0 && health.EwmaMs < 0)
                {
                    continue;
                }

                dto.Addresses[pair.Key] = new HealthEntryDto
                {
                    Failures = health.Failures,
                    LastFailureMs = Volatile.Read(ref health.LastFailureUnixMs),
                    EwmaMs = health.EwmaMs
                };
            }

            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var temp = path + ".tmp";

            File.WriteAllText(temp, JsonSerializer.Serialize(dto));
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
