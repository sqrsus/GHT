namespace GHT.Models;

/// <summary>
/// 代理运行期计数器（无锁自增，UI 每秒采样一次）。
/// 分开展示「池胜 / 预算回落 / 直连」是判断加速是否真的生效的唯一凭据：
/// 只有「池胜」占比高，才说明加速池与竞速在起作用，而不是一直在偷偷走系统 DNS。
///
/// 另外记录字节数与首连耗时分布：没有这两个量，「加速到底快了多少」只能靠感觉。
/// 首连耗时用固定长度的环形缓冲（不累积全量历史），避免长时间运行后内存无限增长。
/// </summary>
public sealed class ProxyStatistics
{
    private const int SampleCapacity = 256;

    private long _requests;
    private long _races;
    private long _tunnels;
    private long _errors;
    private long _poolWins;
    private long _budgetFallbacks;
    private long _directConnects;
    private long _bytesUp;
    private long _bytesDown;
    private long _activeTunnels;

    private readonly int[] _connectSamples = new int[SampleCapacity];
    private readonly object _sampleGate = new();
    private int _sampleIndex;
    private int _sampleCount;

    private string _lastWinner = string.Empty;
    private string _lastFailure = string.Empty;
    private readonly object _textGate = new();

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

    /// <summary>隧道建立（活跃数 +1）。</summary>
    public void TunnelOpened() => Interlocked.Increment(ref _activeTunnels);

    /// <summary>隧道结束（活跃数 -1）。</summary>
    public void TunnelClosed() => Interlocked.Decrement(ref _activeTunnels);

    public void AddBytesUploaded(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _bytesUp, bytes);
        }
    }

    public void AddBytesDownloaded(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _bytesDown, bytes);
        }
    }

    /// <summary>记录一次「选路到建连成功」的耗时（毫秒）。</summary>
    public void AddConnectSample(int milliseconds)
    {
        if (milliseconds < 0)
        {
            return;
        }

        lock (_sampleGate)
        {
            _connectSamples[_sampleIndex] = milliseconds;
            _sampleIndex = (_sampleIndex + 1) % SampleCapacity;

            if (_sampleCount < SampleCapacity)
            {
                _sampleCount++;
            }
        }
    }

    public void SetWinner(string host, string ip)
    {
        lock (_textGate)
        {
            _lastWinner = $"{host} → {ip}";
        }
    }

    /// <summary>记下最近一次失败的域名，UI 直接展示，省得让用户去翻日志。</summary>
    public void NoteFailure(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return;
        }

        lock (_textGate)
        {
            _lastFailure = host;
        }
    }

    public ProxyStatsSnapshot Snapshot()
    {
        string winner;
        string failure;
        lock (_textGate)
        {
            winner = _lastWinner;
            failure = _lastFailure;
        }

        var samples = TakeSamples();

        return new ProxyStatsSnapshot(
            Interlocked.Read(ref _requests),
            Interlocked.Read(ref _races),
            Interlocked.Read(ref _tunnels),
            Interlocked.Read(ref _errors),
            Interlocked.Read(ref _poolWins),
            Interlocked.Read(ref _budgetFallbacks),
            Interlocked.Read(ref _directConnects),
            Interlocked.Read(ref _bytesUp),
            Interlocked.Read(ref _bytesDown),
            Interlocked.Read(ref _activeTunnels),
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples.Length == 0 ? 0 : (int)samples.Average(),
            winner,
            failure);
    }

    private int[] TakeSamples()
    {
        lock (_sampleGate)
        {
            if (_sampleCount == 0)
            {
                return Array.Empty<int>();
            }

            var copy = new int[_sampleCount];
            Array.Copy(_connectSamples, copy, _sampleCount);
            return copy;
        }
    }

    private static int Percentile(int[] sortedSource, double percentile)
    {
        if (sortedSource.Length == 0)
        {
            return 0;
        }

        Array.Sort(sortedSource);
        var index = (int)Math.Ceiling(percentile * sortedSource.Length) - 1;
        return sortedSource[Math.Clamp(index, 0, sortedSource.Length - 1)];
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
    long BytesUp,
    long BytesDown,
    long ActiveTunnels,
    int ConnectP50Ms,
    int ConnectP95Ms,
    int ConnectAvgMs,
    string LastWinner,
    string LastFailure);
