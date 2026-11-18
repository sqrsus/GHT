using System.Net;
using System.Net.Sockets;

namespace GHT.Services.Dns;

/// <summary>极简 UDP DNS 客户端：一问一答，超时即放弃（失败由调用方降级处理）。</summary>
internal static class UdpDnsClient
{
    private const int MaxResponseBytes = 4096;

    private static int _sequence;

    /// <summary>
    /// 查询单条记录。<paramref name="ipv6"/> 为 true 时查 AAAA。
    /// socket 用 Connect 绑定对端，内核会丢掉其他来源的报文，因此不需要额外做来源校验。
    /// </summary>
    public static async Task<DnsQueryResult?> QueryAsync(
        IPAddress server,
        string host,
        int timeoutMs,
        CancellationToken cancellationToken,
        bool ipv6 = false)
    {
        var id = NextId();
        var query = DnsMessage.BuildQuery(host, id, ipv6);
        if (query is null)
        {
            // 域名本身不合法（非法字符 / 标签超长），发出去也只会得到畸形答案
            return null;
        }

        try
        {
            using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(server, 53));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeoutMs > 0)
            {
                timeout.CancelAfter(timeoutMs);
            }

            await socket.SendAsync(query, SocketFlags.None, timeout.Token).ConfigureAwait(false);

            var buffer = new byte[MaxResponseBytes];
            var received = await socket
                .ReceiveAsync(buffer.AsMemory(0, buffer.Length), SocketFlags.None, timeout.Token)
                .ConfigureAwait(false);

            if (received <= 0)
            {
                return null;
            }

            return DnsMessage.Parse(new ReadOnlySpan<byte>(buffer, 0, received), id);
        }
        catch
        {
            return null;
        }
    }

    private static ushort NextId()
    {
        var value = Interlocked.Increment(ref _sequence) & 0x7FFF;
        return (ushort)(value == 0 ? 1 : value);
    }
}
