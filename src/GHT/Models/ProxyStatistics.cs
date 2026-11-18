namespace GHT.Models;

/// <summary>代理运行期计数器（无锁自增，UI 每秒采样一次）。</summary>
public sealed class ProxyStatistics
{
    private long _requests;
    private long _races;
    private long _tunnels;
    private long _errors;
    private string _lastWinner = string.Empty;
    private readonly object _gate = new();

    public void AddRequest() => Interlocked.Increment(ref _requests);

    public void AddRace() => Interlocked.Increment(ref _races);

    public void AddTunnel() => Interlocked.Increment(ref _tunnels);

    public void AddError() => Interlocked.Increment(ref _errors);

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
            winner);
    }
}

public readonly record struct ProxyStatsSnapshot(
    long Requests,
    long Races,
    long Tunnels,
    long Errors,
    string LastWinner);
