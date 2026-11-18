using System.Net;
using GHT.Services.Dns;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 地址合法性过滤。这些用例来自真实日志：某个公共 DNS 池成员把 <c>api.github.com</c>
/// 解析到 <c>127.0.0.1</c>、把 <c>collector.github.com</c> 解析到 <c>0.0.0.0</c> 与 <c>::</c>，
/// 这些地址进池后占竞速名额、污染健康度、把 TLS 探测槽耗在连本机上。
///
/// 反向用例同样重要：130.211.x.x / 20.x.x.x 是 GitHub 自己的入口段，
/// 「看着像内网就丢」的规则会直接断掉加速。
/// </summary>
public sealed class AddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]      // 回环：投毒最典型的产物，日志里反复出现
    [InlineData("127.1.2.3")]      // 整个 127/8 都算回环
    [InlineData("0.0.0.0")]        // 未指定：另一种常见投毒答案
    [InlineData("10.0.0.1")]       // 私有
    [InlineData("172.16.5.4")]     // 私有
    [InlineData("192.168.1.1")]    // 私有
    [InlineData("169.254.1.1")]    // 链路本地
    [InlineData("100.64.0.1")]     // CGNAT
    [InlineData("192.0.2.10")]     // TEST-NET-1
    [InlineData("198.51.100.7")]   // TEST-NET-2
    [InlineData("203.0.113.9")]    // TEST-NET-3
    [InlineData("198.18.0.1")]     // 基准测试段
    [InlineData("224.0.0.1")]      // 组播
    [InlineData("255.255.255.255")]// 广播
    [InlineData("240.0.0.1")]      // 保留段
    [InlineData("::")]             // 未指定
    [InlineData("::1")]            // 回环
    [InlineData("fe80::1")]        // 链路本地
    [InlineData("fd00::1")]        // 唯一本地地址
    [InlineData("ff02::1")]        // 组播
    [InlineData("2001:db8::1")]    // 文档段
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped 的回环，必须按 v4 规则判
    public void Rejects_addresses_that_cannot_be_a_public_endpoint(string text)
    {
        Assert.False(AddressPolicy.IsRoutable(IPAddress.Parse(text)), text);
    }

    [Theory]
    [InlineData("20.205.243.166")]     // github.com 常见入口
    [InlineData("20.27.177.113")]      // japaneast 边缘
    [InlineData("185.199.108.153")]    // Pages
    [InlineData("140.82.112.4")]       // GitHub 自有 AS
    [InlineData("4.195.217.80")]       // 日志里胜出的 Azure 地址
    [InlineData("1.1.1.1")]            // 公共 DNS 自身也可能被当作候选
    [InlineData("57.155.223.240")]     // 国内公共 DNS 对 github.com 的实际应答（实测可达）
    [InlineData("20.6.7.17")]          // 另一组国内 DNS 的实际应答（实测可达）
    [InlineData("2606:50c0:8000::215")] // GitHub 真实 IPv6
    [InlineData("::ffff:20.205.243.166")] // IPv4-mapped 的合法地址要放行
    public void Accepts_public_endpoints(string text)
    {
        Assert.True(AddressPolicy.IsRoutable(IPAddress.Parse(text)), text);
    }

    /// <summary>
    /// 边界用例：<c>198.51.44.8</c> <b>不是</b>保留段。
    /// TEST-NET-2 只是 <c>198.51.100.0/24</c>，<c>198.51.0.0/16</c> 的其余部分已分配给真实组织。
    /// 实测某台公共 DNS 把它当作 github.com 的答案返回，但那是「一台连不上的真实主机」，
    /// 不是投毒特征 —— 拦它属于误杀，该由健康度与退避去处理。
    /// 这个用例存在的意义就是防止后来者「顺手把 198.51/16 整段拉黑」。
    /// </summary>
    [Fact]
    public void Does_not_reject_a_real_host_that_merely_looks_like_a_document_range()
    {
        Assert.True(AddressPolicy.IsRoutable(IPAddress.Parse("198.51.44.8")));
        Assert.False(AddressPolicy.IsRoutable(IPAddress.Parse("198.51.100.8")));
    }

    [Fact]
    public void Rejects_null()
    {
        Assert.False(AddressPolicy.IsRoutable(null));
    }
}
