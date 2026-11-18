using System.Net;

namespace GHT.Services;

/// <summary>
/// 候选合并：把「DNS 当前答案」与「GitHub 官方静态地址」交错成一条竞速队列。
///
/// 抽成独立类型不是为了复用，而是因为这段顺序<b>直接决定首批 8 个名额的构成</b>，
/// 而在 ProxyServer 里它是私有方法、依赖一整串服务，没法单独验证。
/// </summary>
internal static class CandidateMerger
{
    /// <summary>每插入 1 个官方地址之前，先放几个 DNS 候选。</summary>
    public const int DefaultDnsPerSeed = 3;

    /// <summary>
    /// 按 3:1 交错合并。
    ///
    /// 为什么必须交错而不是追加：一批竞速最多只取 <c>RaceMaxIps</c>（默认 8）个，
    /// 如果静态地址整段排在后面，首批就永远轮不到它们，「换一个区域试试」只能等到重赛才发生。
    ///
    /// 为什么交错必须节流：官方地址里存在「TCP 能通、TLS 直接失败」的样本
    /// （实测随机抽 4 个就有 2 个被 TLS 拒绝）。若一半名额被它们占掉，首批就等于拿坏地址去赌。
    /// 按 3:1 交错后，8 个名额里官方地址最多占 2 个 —— 既保留「换区域」的机会，
    /// 又保证大部分名额留给「被 DNS 认可的」候选。
    /// </summary>
    public static List<IPAddress> Merge(
        IReadOnlyList<IPAddress> primary,
        IReadOnlyList<IPAddress> seeds,
        int dnsPerSeed = DefaultDnsPerSeed)
    {
        if (seeds.Count == 0)
        {
            return new List<IPAddress>(primary);
        }

        var step = Math.Max(1, dnsPerSeed);
        var merged = new List<IPAddress>(primary.Count + seeds.Count);
        var seen = new HashSet<IPAddress>(primary.Count + seeds.Count);

        var primaryIndex = 0;
        var seedIndex = 0;

        // 两个游标同时推进，任何一轮至少消耗一个输入，所以必然终止
        while (primaryIndex < primary.Count || seedIndex < seeds.Count)
        {
            for (var i = 0; i < step && primaryIndex < primary.Count; i++)
            {
                if (seen.Add(primary[primaryIndex]))
                {
                    merged.Add(primary[primaryIndex]);
                }

                primaryIndex++;
            }

            if (seedIndex < seeds.Count)
            {
                if (seen.Add(seeds[seedIndex]))
                {
                    merged.Add(seeds[seedIndex]);
                }

                seedIndex++;
            }
        }

        return merged;
    }
}
