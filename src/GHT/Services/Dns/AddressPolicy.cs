using System.Net;
using System.Net.Sockets;

namespace GHT.Services.Dns;

/// <summary>
/// 候选地址的合法性判定：只收「能在公网上路由到某个真实对端」的地址。
///
/// 为什么必须有这一层：加速池里混着被投毒或被劫持的公共 DNS，它们对查询的「回答」是
/// <c>127.0.0.1</c>、<c>::1</c>、<c>0.0.0.0</c>、<c>::</c> 这类本机或保留地址。这些地址在竞速里
/// 表现为「瞬间 ConnectionRefused」，看起来失败得很快、无害，实际后果是：
/// <list type="number">
/// <item>占掉一批竞速名额（8 个名额里混进 3 个本机地址，真正在比的只剩 5 个）；</item>
/// <item>污染地址健康度：每次都被记一次失败，让「近期失败」这个信号的基数失真；</item>
/// <item>被 TLS 校验反复探测，每个都要等一次超时，把 <c>MaxConcurrentProbes</c> 的槽位占满，
///       真正有用的探测只能排队。</item>
/// </list>
/// 这些地址对「访问公网 GitHub」这个目标没有任何意义，在入池前就该丢掉，
/// 而不是留到竞速阶段靠失败来淘汰。
///
/// 反向的边界也要守住：<b>只</b>排除明确不可路由的段，不做「看着像内网就丢」的猜测 ——
/// 130.211.x.x、20.x.x.x 这些公有云地址段是 GitHub 自己的入口，误杀会直接断掉加速。
/// </summary>
internal static class AddressPolicy
{
    /// <summary>该地址是否可能作为公网服务端被连接。</summary>
    public static bool IsRoutable(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsRoutableV4(address),
            AddressFamily.InterNetworkV6 => IsRoutableV6(address),
            _ => false
        };
    }

    private static bool IsRoutableV4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

        return value switch
        {
            // 0.0.0.0/8：本网络 / 未指定地址。大量投毒回答就是 0.0.0.0
            var v when (v & 0xFF000000) == 0x00000000 => false,

            // 10.0.0.0/8、172.16.0.0/12、192.168.0.0/16：私有地址
            var v when (v & 0xFF000000) == 0x0A000000 => false,
            var v when (v & 0xFFF00000) == 0xAC100000 => false,
            var v when (v & 0xFFFF0000) == 0xC0A80000 => false,

            // 100.64.0.0/10：运营商级 NAT，家宽常见但方向是「别人连不进来」
            var v when (v & 0xFFC00000) == 0x64400000 => false,

            // 127.0.0.0/8：回环。投毒最典型的产物，也是日志里反复出现的那一类
            var v when (v & 0xFF000000) == 0x7F000000 => false,

            // 169.254.0.0/16：链路本地（含云环境的元数据地址 169.254.169.254，连它没有意义）
            var v when (v & 0xFFFF0000) == 0xA9FE0000 => false,

            // 192.0.0.0/24：IETF 协议分配段
            var v when (v & 0xFFFFFF00) == 0xC0000000 => false,

            // 192.0.2.0/24、198.51.100.0/24、203.0.113.0/24：TEST-NET-1/2/3
            var v when (v & 0xFFFFFF00) == 0xC0000200 => false,
            var v when (v & 0xFFFFFF00) == 0xC6336400 => false,
            var v when (v & 0xFFFFFF00) == 0xCB007100 => false,

            // 198.18.0.0/15：基准测试保留段
            var v when (v & 0xFFFE0000) == 0xC6120000 => false,

            // 224.0.0.0/4：组播
            var v when (v & 0xF0000000) == 0xE0000000 => false,

            // 240.0.0.0/4：保留段，含 255.255.255.255 广播
            var v when (v & 0xF0000000) == 0xF0000000 => false,

            _ => true
        };
    }

    private static bool IsRoutableV6(IPAddress address)
    {
        // IPv4-mapped（::ffff:a.b.c.d）按 IPv4 规则判，否则 ::ffff:127.0.0.1 会被当成普通 v6 放行
        if (address.IsIPv4MappedToIPv6)
        {
            return IsRoutableV4(address.MapToIPv4());
        }

        var bytes = address.GetAddressBytes();

        // ::（未指定）与 ::1（回环）
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback))
        {
            return false;
        }

        // fc00::/7：唯一本地地址（内网）
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return false;
        }

        // fe80::/10：链路本地
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
        {
            return false;
        }

        // ff00::/8：组播
        if (bytes[0] == 0xFF)
        {
            return false;
        }

        // 2001:db8::/32：文档示例段
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
        {
            return false;
        }

        // 只接受 2000::/3 这段全球单播：其余（100::/64 丢弃段、5f00::/8、::/96 兼容地址等）
        // 都不是「可以被连上的公网服务端」。注意 2001:4860::/32、2606:50c0::/32 这类都能通过。
        return (bytes[0] & 0xE0) == 0x20;
    }
}
