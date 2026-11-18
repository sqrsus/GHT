using System.Net;
using GHT.Services;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// TLS 校验闸门的纯逻辑部分（缓存与分拣）。真实握手不在这里测 —— 那需要外网，
/// 而这一段最容易出错的恰恰是「已知坏 / 未验证 / 已过期」三种状态的判定。
/// </summary>
public sealed class UpstreamValidatorTests
{
    private static readonly IPAddress Good = IPAddress.Parse("20.27.177.113");
    private static readonly IPAddress Bad = IPAddress.Parse("20.205.243.160");
    private static readonly IPAddress Unknown = IPAddress.Parse("20.247.166.64");

    private static LogService NewLog() =>
        new(Path.Combine(Path.GetTempPath(), "ght-validator-" + Guid.NewGuid().ToString("N")));

    private static UpstreamValidator NewValidator(LogService log, TimeSpan? ttl = null)
    {
        var lifespan = ttl ?? TimeSpan.FromMinutes(10);
        return new UpstreamValidator(log, lifespan, lifespan);
    }

    [Fact]
    public void Cold_cache_marks_everything_unverified()
    {
        using var log = NewLog();
        var validator = NewValidator(log);

        var verdict = validator.Partition("github.com", 443, new[] { Good, Bad, Unknown });

        // 冷启动顺序必须完全不变，否则首个请求会被探测拖慢
        Assert.Empty(verdict.Trusted);
        Assert.Equal(3, verdict.Unverified.Count);
        Assert.Equal(0, verdict.KnownBad);
    }

    [Fact]
    public void Verified_addresses_are_ordered_before_unverified_ones()
    {
        using var log = NewLog();
        var validator = NewValidator(log);

        validator.Record("github.com", Good, ok: true);

        var verdict = validator.Partition("github.com", 443, new[] { Unknown, Good });

        Assert.Single(verdict.Trusted);
        Assert.Equal(Good, verdict.Trusted[0]);
        Assert.Single(verdict.Unverified);
        Assert.Equal(Unknown, verdict.Unverified[0]);
    }

    [Fact]
    public void Rejected_addresses_are_dropped_instead_of_ranked_last()
    {
        using var log = NewLog();
        var validator = NewValidator(log);

        validator.Record("github.com", Bad, ok: false);

        var verdict = validator.Partition("github.com", 443, new[] { Bad, Good });

        // KnownBad 必须被剔除：把它留在候选里只会白占一个竞速名额
        Assert.Empty(verdict.Trusted);
        Assert.Single(verdict.Unverified);
        Assert.Equal(Good, verdict.Unverified[0]);
        Assert.Equal(1, verdict.KnownBad);
    }

    [Fact]
    public void Verdicts_are_scoped_per_host()
    {
        using var log = NewLog();
        var validator = NewValidator(log);

        validator.Record("github.com", Good, ok: true);

        // 同一个 IP 服务 github.com 通过，不代表它是 api.github.com 的入口
        var other = validator.Partition("api.github.com", 443, new[] { Good });

        Assert.Empty(other.Trusted);
        Assert.Single(other.Unverified);
    }

    [Fact]
    public void Plain_http_ports_skip_the_gate()
    {
        using var log = NewLog();
        var validator = NewValidator(log);

        var verdict = validator.Partition("github.com", 80, new[] { Good, Bad });

        Assert.Equal(2, verdict.Trusted.Count);
        Assert.Empty(verdict.Unverified);
    }

    [Fact]
    public void Expired_verdicts_fall_back_to_unverified()
    {
        using var log = NewLog();
        var validator = NewValidator(log, TimeSpan.FromMilliseconds(-1));

        validator.Record("github.com", Good, ok: true);
        validator.Record("github.com", Bad, ok: false);

        var verdict = validator.Partition("github.com", 443, new[] { Good, Bad });

        // 过期结论不能继续当作可信/不可信：地址状态会变，宁可重新探测
        Assert.Empty(verdict.Trusted);
        Assert.Equal(2, verdict.Unverified.Count);
        Assert.Equal(0, verdict.KnownBad);
    }

    [Fact]
    public void Peek_reports_raw_verdicts()
    {
        using var log = NewLog();
        var validator = NewValidator(log);

        Assert.Null(validator.Peek("github.com", Good));

        validator.Record("github.com", Good, ok: true);
        validator.Record("github.com", Bad, ok: false);

        Assert.True(validator.Peek("github.com", Good));
        Assert.False(validator.Peek("github.com", Bad));
    }

    [Fact]
    public void Host_keys_are_normalized()
    {
        using var log = NewLog();
        var validator = NewValidator(log);

        validator.Record("GitHub.COM.", Good, ok: true);

        var verdict = validator.Partition("github.com", 443, new[] { Good });

        Assert.Single(verdict.Trusted);
    }
}
