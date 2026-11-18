using GHT.Models;
using GHT.Services;
using Xunit;

namespace GHT.Tests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData(80, 7890)]
    [InlineData(0, 7890)]
    [InlineData(70000, 7890)]
    [InlineData(7891, 7891)]
    public void Normalizes_port(int input, int expected)
    {
        var settings = new AppSettings { ProxyPort = input };
        SettingsStore.Normalize(settings);
        Assert.Equal(expected, settings.ProxyPort);
    }

    [Fact]
    public void Negative_budget_falls_back_to_default()
    {
        var settings = new AppSettings { ConnectBudgetMs = -1 };
        SettingsStore.Normalize(settings);
        Assert.Equal(1500, settings.ConnectBudgetMs);
    }

    [Fact]
    public void Zero_budget_is_kept_as_rollback_switch()
    {
        var settings = new AppSettings { ConnectBudgetMs = 0 };
        SettingsStore.Normalize(settings);
        Assert.Equal(0, settings.ConnectBudgetMs);
    }

    [Fact]
    public void Race_max_ips_is_clamped_to_fallback()
    {
        var settings = new AppSettings { RaceMaxIps = 0 };
        SettingsStore.Normalize(settings);
        Assert.Equal(8, settings.RaceMaxIps);
    }

    [Fact]
    public void Max_ttl_never_below_min_ttl()
    {
        var settings = new AppSettings { DnsCacheMinTtlSec = 300, DnsCacheMaxTtlSec = 60 };
        SettingsStore.Normalize(settings);
        Assert.True(settings.DnsCacheMaxTtlSec >= settings.DnsCacheMinTtlSec);
    }

    [Fact]
    public void Pool_is_deduplicated_and_sorted_by_latency()
    {
        var settings = new AppSettings
        {
            DnsPool = new List<DnsPoolEntry>
            {
                new() { Ip = "1.1.1.1", LatencyMs = 50 },
                new() { Ip = "1.1.1.1", LatencyMs = 10 },
                new() { Ip = "8.8.8.8", LatencyMs = 20 },
                new() { Ip = "   ", LatencyMs = 1 }
            }
        };

        SettingsStore.Normalize(settings);

        Assert.Equal(2, settings.DnsPool.Count);
        Assert.Equal("8.8.8.8", settings.DnsPool[0].Ip);
    }

    [Fact]
    public void Unknown_close_action_falls_back_to_ask()
    {
        var settings = new AppSettings { CloseAction = "Whatever" };
        SettingsStore.Normalize(settings);
        Assert.Equal("Ask", settings.CloseAction);
    }

    [Fact]
    public void Null_collections_are_repaired()
    {
        var settings = new AppSettings
        {
            GlobalDnsWhitelist = null!,
            DnsPool = null!,
            DnsPoolDomain = "  "
        };

        SettingsStore.Normalize(settings);

        Assert.Equal(string.Empty, settings.GlobalDnsWhitelist);
        Assert.Empty(settings.DnsPool);
        Assert.Equal("github.com", settings.DnsPoolDomain);
    }

    [Fact]
    public void Default_whitelist_covers_github_assets()
    {
        var list = DomainWildcardList.Parse(AppSettings.DefaultWhitelist);

        Assert.True(list.IsMatch("github.com"));
        Assert.True(list.IsMatch("objects.githubusercontent.com"));
        Assert.True(list.IsMatch("github.githubassets.com"));
    }
}
