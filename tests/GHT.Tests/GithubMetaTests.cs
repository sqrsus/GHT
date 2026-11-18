using System.Net;
using GHT.Services;
using GHT.Services.Github;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 官方地址（api.github.com/meta）接入的三条纪律，每一条错了都会直接让加速变差：
/// 1) 只取 web/git/api/pages，绝不把 hooks/actions 这类「GitHub 连你」的地址当出口；
/// 2) 大网段不猜偏移（/24 及更宽一律跳过），小网段取段首几个并由 TLS 校验兜底；
/// 3) 每天轮换候选窗口，否则永远只试同一两个区域，健康度也学不到东西。
/// </summary>
public sealed class GithubMetaTests
{
    private const string SampleMeta = """
    {
      "hooks": ["9.234.106.48/28"],
      "web": ["20.205.243.166/32", "140.82.112.0/20", "20.247.166.64/28"],
      "git": ["20.27.177.113/32"],
      "api": ["20.87.245.6/32"],
      "pages": ["185.199.108.153/32"],
      "importer": ["52.23.85.212/32"],
      "actions": ["4.148.0.0/16", "20.200.245.244/32"]
    }
    """;

    private static GithubMetaSnapshot ParseSample()
    {
        return GithubMetaProvider.Parse(SampleMeta, DateTimeOffset.UnixEpoch, fromCache: false, stale: false);
    }

    private static GithubMetaSnapshot BuildSnapshot(
        IReadOnlyList<IPAddress>? web = null,
        IReadOnlyList<IPAddress>? git = null,
        IReadOnlyList<IPAddress>? api = null,
        IReadOnlyList<IPAddress>? pages = null)
    {
        return new GithubMetaSnapshot(
            web ?? Array.Empty<IPAddress>(),
            git ?? Array.Empty<IPAddress>(),
            api ?? Array.Empty<IPAddress>(),
            pages ?? Array.Empty<IPAddress>(),
            DateTimeOffset.UnixEpoch,
            fromCache: false,
            stale: false);
    }

    // ── 解析与分组过滤 ──────────────────────────────────────────────────

    [Fact]
    public void Parse_keeps_only_client_facing_groups()
    {
        var snapshot = ParseSample();

        Assert.Single(snapshot.Api);
        Assert.Equal(IPAddress.Parse("20.87.245.6"), snapshot.Api[0]);

        Assert.Single(snapshot.Git);
        Assert.Equal(IPAddress.Parse("20.27.177.113"), snapshot.Git[0]);

        Assert.Single(snapshot.Pages);
        Assert.Equal(IPAddress.Parse("185.199.108.153"), snapshot.Pages[0]);
    }

    [Fact]
    public void Parse_expands_narrow_ranges_and_skips_wide_blocks()
    {
        var snapshot = ParseSample();

        // /32 取自身；/28 取段首两个；/20（GitHub 自有 AS 的连续分配）跳过
        Assert.Equal(3, snapshot.Web.Count);
        Assert.Contains(IPAddress.Parse("20.205.243.166"), snapshot.Web);
        Assert.Contains(IPAddress.Parse("20.247.166.64"), snapshot.Web);
        Assert.Contains(IPAddress.Parse("20.247.166.65"), snapshot.Web);
        Assert.DoesNotContain(IPAddress.Parse("140.82.112.0"), snapshot.Web);
    }

    [Fact]
    public void Parse_never_pulls_inbound_groups()
    {
        var snapshot = ParseSample();

        var all = snapshot.Web.Concat(snapshot.Git).Concat(snapshot.Api).Concat(snapshot.Pages).ToList();

        // hooks 的 9.234.106.48/28、actions 的 4.148.0.0/16 与 20.200.245.244 都不该出现
        Assert.DoesNotContain(IPAddress.Parse("9.234.106.48"), all);
        Assert.DoesNotContain(IPAddress.Parse("4.148.0.0"), all);
        Assert.DoesNotContain(IPAddress.Parse("20.200.245.244"), all);
        Assert.DoesNotContain(IPAddress.Parse("52.23.85.212"), all);

        Assert.Equal(1 + 1 + 1 + 3, snapshot.TotalCount);
    }

    // ── CIDR 展开 ───────────────────────────────────────────────────────

    [Fact]
    public void ExpandCidr_single_host_yields_itself()
    {
        var target = new List<IPAddress>();
        GithubMetaProvider.ExpandCidr("185.199.108.153/32", target);

        Assert.Single(target);
        Assert.Equal(IPAddress.Parse("185.199.108.153"), target[0]);
    }

    [Fact]
    public void ExpandCidr_slash28_yields_network_and_next()
    {
        var target = new List<IPAddress>();
        GithubMetaProvider.ExpandCidr("20.247.166.64/28", target);

        Assert.Equal(2, target.Count);
        Assert.Equal(IPAddress.Parse("20.247.166.64"), target[0]);
        Assert.Equal(IPAddress.Parse("20.247.166.65"), target[1]);
    }

    [Fact]
    public void ExpandCidr_skips_network_and_broadcast_octets()
    {
        var target = new List<IPAddress>();
        GithubMetaProvider.ExpandCidr("20.6.7.0/28", target);

        // .0 是网络地址，不能当入口用
        Assert.Single(target);
        Assert.Equal(IPAddress.Parse("20.6.7.1"), target[0]);
    }

    [Fact]
    public void ExpandCidr_ignores_wide_blocks_and_ipv6()
    {
        var target = new List<IPAddress>();

        GithubMetaProvider.ExpandCidr("185.199.108.0/22", target);
        GithubMetaProvider.ExpandCidr("140.82.112.0/20", target);
        GithubMetaProvider.ExpandCidr("2606:50c0::/32", target);
        GithubMetaProvider.ExpandCidr("not-a-cidr", target);

        Assert.Empty(target);
    }

    // ── 主机绑定 ────────────────────────────────────────────────────────

    [Fact]
    public void SelectGroups_maps_github_com_to_web_and_git()
    {
        var web = new[] { IPAddress.Parse("20.205.243.166") };
        var git = new[] { IPAddress.Parse("20.27.177.113") };
        var pages = new[] { IPAddress.Parse("185.199.108.153") };

        var groups = GithubMetaProvider.SelectGroups("github.com", BuildSnapshot(web, git, pages: pages));

        Assert.Equal(2, groups.Count);
        Assert.Same(web, groups[0]);
        Assert.Same(git, groups[1]);
    }

    [Fact]
    public void SelectGroups_keeps_api_and_pages_isolated()
    {
        var api = new[] { IPAddress.Parse("20.87.245.6") };
        var pages = new[] { IPAddress.Parse("185.199.108.153") };
        var snapshot = BuildSnapshot(api: api, pages: pages);

        var apiGroups = GithubMetaProvider.SelectGroups("api.github.com", snapshot);
        Assert.Single(apiGroups);
        Assert.Same(api, apiGroups[0]);

        // Pages 的地址拿去服务 github.com 会返回 404，因此只能给 *.github.io
        var ioGroups = GithubMetaProvider.SelectGroups("sqrsus.github.io", snapshot);
        Assert.Single(ioGroups);
        Assert.Same(pages, ioGroups[0]);
    }

    [Fact]
    public void SelectGroups_returns_nothing_for_hosts_meta_does_not_cover()
    {
        var snapshot = ParseSample();

        // githubusercontent / githubassets / ghcr.io 在 meta 里没有对应分组，不能猜
        Assert.Empty(GithubMetaProvider.SelectGroups("objects.githubusercontent.com", snapshot));
        Assert.Empty(GithubMetaProvider.SelectGroups("github.githubassets.com", snapshot));
        Assert.Empty(GithubMetaProvider.SelectGroups("ghcr.io", snapshot));
    }

    [Fact]
    public void SelectGroups_normalizes_case_and_trailing_dot()
    {
        var snapshot = ParseSample();

        Assert.NotEmpty(GithubMetaProvider.SelectGroups("GitHub.COM.", snapshot));
        Assert.NotEmpty(GithubMetaProvider.SelectGroups("API.GitHub.com", snapshot));
    }

    // ── 窗口轮换 ────────────────────────────────────────────────────────

    [Fact]
    public void BuildSeeds_is_deterministic_within_a_day()
    {
        var pool = Enumerable.Range(1, 40)
            .Select(i => IPAddress.Parse($"20.200.245.{i}"))
            .ToArray();

        var groups = new List<IReadOnlyList<IPAddress>> { pool };

        var first = GithubMetaProvider.BuildSeeds(groups, day: 100);
        var again = GithubMetaProvider.BuildSeeds(groups, day: 100);

        Assert.Equal(40, first.Count);
        Assert.Equal(first, again);
        Assert.Equal(40, first.Distinct().Count());
        Assert.All(first, address => Assert.Contains(address, pool));
    }

    [Fact]
    public void BuildSeeds_rotates_across_days_instead_of_pinning_one_region()
    {
        var pool = Enumerable.Range(1, 40)
            .Select(i => IPAddress.Parse($"20.200.245.{i}"))
            .ToArray();

        var groups = new List<IReadOnlyList<IPAddress>> { pool };

        var union = new HashSet<IPAddress>();
        for (var day = 1; day <= 366; day++)
        {
            foreach (var address in GithubMetaProvider.BuildSeeds(groups, day).Take(4))
            {
                union.Add(address);
            }
        }

        // 如果按原序取前 N 个，这里只会出现 4 个地址
        Assert.True(union.Count > 4, $"轮换没有生效，一年里只出现过 {union.Count} 个地址");
    }

    [Fact]
    public void BuildSeeds_keeps_the_whole_pool_for_health_ranking()
    {
        var pool = new[] { IPAddress.Parse("20.205.243.166"), IPAddress.Parse("20.27.177.113") };
        var groups = new List<IReadOnlyList<IPAddress>> { pool };

        // 截断交给 UpstreamRacer.RankSeeds：这里若提前截断，健康度就失去作用面
        var seeds = GithubMetaProvider.BuildSeeds(groups, day: 1);

        Assert.Equal(2, seeds.Count);
    }

    [Fact]
    public void BuildSeeds_deduplicates_across_groups()
    {
        var shared = IPAddress.Parse("20.205.243.166");
        var groups = new List<IReadOnlyList<IPAddress>>
        {
            new[] { shared },
            new[] { shared, IPAddress.Parse("20.27.177.113") }
        };

        var seeds = GithubMetaProvider.BuildSeeds(groups, day: 1);

        Assert.Equal(2, seeds.Count);
    }

    [Fact]
    public void StableHash_does_not_depend_on_process_seed()
    {
        // 与 string.GetHashCode 不同：这里必须跨进程稳定，否则每次重启都会选到另一批候选，
        // 地址健康度（EWMA / 退避）就永远学不到东西。
        Assert.Equal(7718030511699221849UL, GithubMetaProvider.StableHash("github.com"));
        Assert.Equal(7072525499931807155UL, GithubMetaProvider.StableHash("api.github.com"));
        Assert.NotEqual(GithubMetaProvider.StableHash("a"), GithubMetaProvider.StableHash("b"));
    }
}
