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
/// </summary>
public sealed class UpstreamRacer
{
    /// <summary>等待落败任务自我清理的上限。取消后它们通常立刻结束。</summary>
    private static readonly TimeSpan ReapTimeout = TimeSpan.FromSeconds(2);

    private readonly LogService _log;

    public UpstreamRacer(LogService log)
    {
        _log = log;
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

        var pending = new List<Task<RaceWin?>>(candidates.Count);
        foreach (var candidate in candidates)
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

    private static async Task<RaceWin?> TryConnectAsync(IPAddress address, int port, CancellationToken token)
    {
        var client = new TcpClient(address.AddressFamily);

        try
        {
            await client.ConnectAsync(address, port, token).ConfigureAwait(false);
            client.NoDelay = true;
            return new RaceWin(address, client);
        }
        catch
        {
            // 失败路径由自己释放；成功的连接交给调用方或 ReapLosersAsync。
            client.Dispose();
            return null;
        }
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
