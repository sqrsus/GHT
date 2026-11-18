using System.Net;
using System.Net.Sockets;

namespace GHT.Services;

/// <summary>竞速胜出者：赢的那个 IP 与已建好的连接（调用方负责释放客户端）。</summary>
public readonly record struct RaceWin(IPAddress Ip, TcpClient Client);

/// <summary>
/// TCP 建连竞速：同时向多个候选 IP 发起 connect，第一个成功的即为上游。
/// 只做握手竞速，不读不写任何业务数据，因此对 TLS 完全透明。
/// </summary>
public sealed class UpstreamRacer
{
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

                foreach (var task in pending)
                {
                    Discard(task);
                }

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
            client.Dispose();
            return null;
        }
    }

    /// <summary>竞速结束后，把「迟到的成功连接」也释放掉，避免句柄泄漏。</summary>
    private static void Discard(Task<RaceWin?> task)
    {
        _ = task.ContinueWith(
            static completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion && completed.Result is { } win)
                {
                    try
                    {
                        win.Client.Dispose();
                    }
                    catch
                    {
                        // ignore
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
