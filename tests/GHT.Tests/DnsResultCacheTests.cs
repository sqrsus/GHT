using System.Net;
using GHT.Services.Dns;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 缓存改成「不可变数组快照」后，有两条语义必须守住：
/// 1) 读到的数组永远是一份完整快照，后续合并不影响已发出的引用；
/// 2) 去重按 IP 相等语义（不是字符串），且 IPv4/IPv6 不会互相顶掉。
/// </summary>
public sealed class DnsResultCacheTests
{
    private static IPAddress Ip(string value) => IPAddress.Parse(value);

    [Fact]
    public void Returns_null_for_unknown_host()
    {
        var cache = new DnsResultCache();
        Assert.Null(cache.Get("github.com"));
    }

    [Fact]
    public void Merges_batches_and_dedupes()
    {
        var cache = new DnsResultCache();

        cache.Merge("github.com", new[] { Ip("1.1.1.1"), Ip("2.2.2.2") }, 60, preferIpv4: true);
        cache.Merge("github.com", new[] { Ip("2.2.2.2"), Ip("3.3.3.3") }, 60, preferIpv4: true);

        var addresses = cache.Get("github.com");

        Assert.NotNull(addresses);
        Assert.Equal(3, addresses!.Count);
        Assert.Contains(addresses, a => a.Equals(Ip("3.3.3.3")));
    }

    [Fact]
    public void Previously_returned_snapshot_is_not_mutated_by_later_merge()
    {
        var cache = new DnsResultCache();

        cache.Merge("github.com", new[] { Ip("1.1.1.1") }, 60, preferIpv4: true);
        var first = cache.Get("github.com");
        Assert.NotNull(first);

        cache.Merge("github.com", new[] { Ip("9.9.9.9") }, 60, preferIpv4: true);
        var second = cache.Get("github.com");

        Assert.Single(first!);
        Assert.Equal(2, second!.Count);
    }

    [Fact]
    public void Keeps_ipv4_and_ipv6_apart_and_orders_ipv4_first()
    {
        var cache = new DnsResultCache();

        cache.Merge("github.com", new[] { Ip("2606:50c0:8000::154"), Ip("20.205.243.166") }, 60, preferIpv4: true);

        var addresses = cache.Get("github.com");

        Assert.NotNull(addresses);
        Assert.Equal(2, addresses!.Count);
        Assert.Equal(IPAddress.Parse("20.205.243.166"), addresses[0]);
    }

    [Fact]
    public void Prefers_ipv6_first_when_configured()
    {
        var cache = new DnsResultCache();

        cache.Merge("github.com", new[] { Ip("20.205.243.166"), Ip("2606:50c0:8000::154") }, 60, preferIpv4: false);

        var addresses = cache.Get("github.com");

        Assert.NotNull(addresses);
        Assert.Equal(IPAddress.Parse("2606:50c0:8000::154"), addresses![0]);
    }

    [Fact]
    public void Clear_removes_every_entry()
    {
        var cache = new DnsResultCache();
        cache.Merge("github.com", new[] { Ip("1.1.1.1") }, 60, preferIpv4: true);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.Get("github.com"));
    }

    [Fact]
    public void Merge_with_empty_batch_does_not_create_entry()
    {
        var cache = new DnsResultCache();

        cache.Merge("github.com", Array.Empty<IPAddress>(), 60, preferIpv4: true);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Short_ttl_address_expires_while_long_ttl_address_stays()
    {
        // 旧实现整条缓存只存一个过期时刻且取最大值：短 TTL 的地址会跟着长 TTL 一起活下去
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var cache = new DnsResultCache(() => clock);

        cache.Merge("github.com", new[] { Ip("1.1.1.1") }, 30, preferIpv4: true);

        clock = clock.AddSeconds(20);
        cache.Merge("github.com", new[] { Ip("2.2.2.2") }, 600, preferIpv4: true);

        // t=35：第一个地址（30s）已过期，第二个还有 585s
        clock = clock.AddSeconds(15);
        var alive = cache.Get("github.com");

        Assert.NotNull(alive);
        Assert.Single(alive!);
        Assert.Equal(Ip("2.2.2.2"), alive![0]);

        // t=635：第二个也过期了
        clock = clock.AddSeconds(600);
        Assert.Null(cache.Get("github.com"));
    }

    [Fact]
    public void Re_confirmed_address_restarts_its_ttl()
    {
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var cache = new DnsResultCache(() => clock);

        cache.Merge("github.com", new[] { Ip("1.1.1.1") }, 60, preferIpv4: true);

        clock = clock.AddSeconds(50);
        cache.Merge("github.com", new[] { Ip("1.1.1.1") }, 60, preferIpv4: true);

        // 距第一次 80s（本该过期），距第二次只有 30s
        clock = clock.AddSeconds(30);
        Assert.NotNull(cache.Get("github.com"));
    }

    [Fact]
    public void Expired_address_is_dropped_without_losing_the_valid_ones()
    {
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var cache = new DnsResultCache(() => clock);

        cache.Merge("github.com", new[] { Ip("1.1.1.1") }, 10, preferIpv4: true);
        clock = clock.AddSeconds(5);
        cache.Merge("github.com", new[] { Ip("2.2.2.2") }, 600, preferIpv4: true);

        clock = clock.AddSeconds(20);

        var alive = cache.Get("github.com");
        Assert.NotNull(alive);
        Assert.DoesNotContain(alive!, a => a.Equals(Ip("1.1.1.1")));
        Assert.Contains(alive!, a => a.Equals(Ip("2.2.2.2")));
    }
}
