using System.Net;
using GHT.Services;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 候选合并顺序。首批只取 <c>RaceMaxIps</c>（默认 8）个，所以「谁排在前 8」直接决定
/// 首批是拿「被 DNS 认可的地址」还是「一批随机远端地址」去竞速 ——
/// 这是日志里「官方地址 4ms 胜出、随即被 TLS 判坏」反复出现的根源。
/// </summary>
public sealed class CandidateMergerTests
{
    private static IPAddress Ip(string text) => IPAddress.Parse(text);

    private static IPAddress[] DnsPool(int count) =>
        Enumerable.Range(1, count).Select(i => Ip($"20.205.243.{i}")).ToArray();

    private static IPAddress[] Seeds(int count) =>
        Enumerable.Range(1, count).Select(i => Ip($"20.27.177.{i}")).ToArray();

    [Fact]
    public void Caps_seeds_in_first_batch_to_about_a_quarter()
    {
        var dns = DnsPool(12);
        var seeds = Seeds(4);

        var merged = CandidateMerger.Merge(dns, seeds);

        var seedSet = new HashSet<IPAddress>(seeds);
        var inFirstBatch = merged.Take(8).Count(seedSet.Contains);

        // 8 个名额里官方地址最多 2 个（3:1 交错的直接结果）
        Assert.Equal(2, inFirstBatch);

        // 但必须至少有一个进得去，否则「换一个区域试试」永远轮不到
        Assert.True(inFirstBatch >= 1);
    }

    [Fact]
    public void Keeps_all_candidates_and_deduplicates()
    {
        var merged = CandidateMerger.Merge(DnsPool(12), Seeds(4));

        Assert.Equal(16, merged.Count);
        Assert.Equal(16, merged.Distinct().Count());
    }

    [Fact]
    public void Returns_primary_untouched_when_there_are_no_seeds()
    {
        var dns = DnsPool(3);

        var merged = CandidateMerger.Merge(dns, Array.Empty<IPAddress>());

        Assert.Equal(dns, merged);
    }

    [Fact]
    public void Appends_leftover_seeds_after_primary_is_exhausted()
    {
        var dns = new[] { Ip("20.205.243.166") };
        var seeds = Seeds(3);

        var merged = CandidateMerger.Merge(dns, seeds);

        Assert.Equal(4, merged.Count);
        Assert.Equal(dns[0], merged[0]);
        Assert.Contains(seeds[0], merged);
        Assert.Contains(seeds[2], merged);
    }

    [Fact]
    public void Deduplicates_overlap_between_dns_and_seeds()
    {
        var shared = Ip("20.205.243.166");

        var merged = CandidateMerger.Merge(new[] { shared }, new[] { shared, Ip("20.27.177.1") });

        Assert.Equal(2, merged.Count);
        Assert.Single(merged, address => address.Equals(shared));
    }

    [Fact]
    public void Uses_seeds_when_dns_gave_nothing()
    {
        var seeds = Seeds(2);

        var merged = CandidateMerger.Merge(Array.Empty<IPAddress>(), seeds);

        Assert.Equal(2, merged.Count);
        Assert.Equal(seeds[0], merged[0]);
    }

    [Fact]
    public void Terminates_when_every_candidate_is_a_duplicate()
    {
        var same = Ip("20.205.243.166");

        var merged = CandidateMerger.Merge(new[] { same, same }, new[] { same, same });

        Assert.Single(merged);
    }
}
