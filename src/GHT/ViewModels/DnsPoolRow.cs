using System.ComponentModel;
using System.Runtime.CompilerServices;
using GHT.Models;

namespace GHT.ViewModels;

/// <summary>
/// 加速池表格的一行。除了扫描时的静态延迟，还带运行期的健康状态 ——
/// 用户需要看到的是「这台 DNS 现在还灵不灵」，而不是一次扫描的历史快照。
/// </summary>
public sealed class DnsPoolRow : INotifyPropertyChanged
{
    private string _status = "待测";
    private string _score = "-";

    public DnsPoolRow(DnsPoolEntry entry)
    {
        Ip = entry.Ip;
        Name = entry.Name;
        LatencyMs = entry.LatencyMs;
    }

    public string Ip { get; }

    public string Name { get; }

    public int LatencyMs { get; }

    /// <summary>健康 / 降级 / 失效 / 待测。</summary>
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    /// <summary>成功 / 失败次数，形如 <c>12/1</c>。</summary>
    public string Score
    {
        get => _score;
        set => Set(ref _score, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateHealth(int ok, int fail)
    {
        Score = ok + fail == 0 ? "-" : $"{ok}/{fail}";

        Status = ok + fail == 0
            ? "待测"
            : fail == 0
                ? "健康"
                : ok >= fail
                    ? "降级"
                    : "失效";
    }

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
