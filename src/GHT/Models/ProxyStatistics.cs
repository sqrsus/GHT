namespace GHT.Models;

/// <summary>
/// 代理运行期计数器（无锁自增，UI 每秒采样一次）。
/// 分开展示「池胜 / 预算回落 / 直连」是判断加速是否真的生效的唯一凭据：
/// 只有「池胜」占比高，才说明加速池与竞速在起作用，而不是一直在偷偷走系统 DNS。
/// </summary>
public sealed class ProxyStatistics
{
    private long _requests;
    private long _races;
    private long _tunnels;
    private long _errors;
    private long _poolWins;
    private long _budgetFallbacks;
    private long _directConnects;
    private string _lastWinner = string.Empty;
    private readonly object _gate = new();

    public void AddRequest() => Interlocked.Increment(ref _requests);

    public void AddRace() => Interlocked.Increment(ref _races);

    public void AddTunnel() => Interlocked.Increment(ref _tunnels);

    public void AddError() => Interlocked.Increment(ref _errors);

    /// <summary>加速池竞速选路成功。</summary>
    public void AddPoolWin() => Interlocked.Increment(ref _poolWins);

    /// <summary>首连预算耗尽，先回落系统 DNS（后台扫描仍在继续）。</summary>
    public void AddBudgetFallback() => Interlocked.Increment(ref _budgetFallbacks);

    /// <summary>直接使用系统 DNS 建连（不命中白名单，或预算/候选耗尽后的回落）。</summary>
    public void AddDirect() => Interlocked.Increment(ref _directConnects);

    public void SetWinner(string host, string ip)
    {
        lock (_gate)
        {
            _lastWinner = $"{host} → {ip}";
        }
    }

    public ProxyStatsSnapshot Snapshot()
    {
        string winner;
        lock (_gate)
        {
            winner = _lastWinner;
        }

        return new ProxyStatsSnapshot(
            Interlocked.Read(ref _requests),
            Interlocked.Read(ref _races),
            Interlocked.Read(ref _tunnels),
            Interlocked.Read(ref _errors),
            Interlocked.Read(ref _poolWins),
            Interlocked.Read(ref _budgetFallbacks),
            Interlocked.Read(ref _directConnects),
            winner);
    }
}

public readonly record struct ProxyStatsSnapshot(
    long Requests,
    long Races,
    long Tunnels,
    long Errors,
    long PoolWins,
    long BudgetFallbacks,
    long DirectConnects,
    string LastWinner);
