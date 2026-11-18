using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

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

    public UpstreamRacer(LogService log)
    {
        _log = log;
    }

    private sealed class AddressHealth
    {
        public int Failures;
        public long LastFailureTicks;

        /// <summary>建连耗时指数滑动平均；负数表示还没有样本。</summary>
        public double EwmaMs = -1;
    }

    public async Task<RaceWin?> RaceAsync(
        IReadOnlyList<IPAddress> candidates,
        int port,
        int connectTimeoutMs,
        int batchBudgetMs,
        CancellationToken cancellationToken)
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
            pending.Add(TryConnectAsync(candidate, port, scope.Token));
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

        var now = Environment.TickCount64;
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
                    if ((now - health.LastFailureTicks) / 1000.0 < backoff)
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

    private async Task<RaceWin?> TryConnectAsync(IPAddress address, int port, CancellationToken token)
    {
        var client = new TcpClient(address.AddressFamily);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 这里刻意<b>不</b>调整套接字缓冲：竞速时可能有 8 个候选同时建连，
            // 给每个都设 256KB 等于把内核缓冲成本乘以候选数，而落败者在几十毫秒内就被释放。
            // 调大缓冲放在胜出之后做（ProxyServer.TuneSocket），只对真正跑流量的那一条生效。
            await client.ConnectAsync(address, port, token).ConfigureAwait(false);
            client.NoDelay = true;
            stopwatch.Stop();

            RecordSuccess(address, stopwatch.Elapsed.TotalMilliseconds);
            return new RaceWin(address, client);
        }
        catch
        {
            // 失败路径由自己释放；成功的连接交给调用方或 ReapLosersAsync。
            RecordFailure(address);
            client.Dispose();
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
        Volatile.Write(ref health.LastFailureTicks, Environment.TickCount64);
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
