using System.Text;

namespace GHT.Services;

/// <summary>请求/响应不符合 HTTP 格式时抛出（对应 400）。</summary>
public sealed class ProxyProtocolException : Exception
{
    public ProxyProtocolException(string message) : base(message)
    {
    }
}

/// <summary>加速池与系统 DNS 都明确失败时抛出（对应 502）。</summary>
public sealed class UpstreamUnavailableException : Exception
{
    public UpstreamUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>HTTP 代理需要的那点协议常量与小工具。</summary>
public static class ProxyProtocol
{
    public static readonly byte[] ConnectEstablished =
        Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\nProxy-Agent: GHT/1.0\r\n\r\n");

    /// <summary>逐跳头：只对当前这一跳有意义，转发时必须剔除，否则会污染下游连接语义。</summary>
    public static bool IsHopByHopHeader(string name) =>
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
        || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    public static byte[] BuildErrorResponse(int statusCode, string reasonPhrase, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var head = $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
                   "Content-Type: text/plain; charset=utf-8\r\n" +
                   $"Content-Length: {body.Length}\r\n" +
                   "Cache-Control: no-store\r\n" +
                   "Connection: close\r\n\r\n";

        var headBytes = Encoding.ASCII.GetBytes(head);
        var response = new byte[headBytes.Length + body.Length];
        Buffer.BlockCopy(headBytes, 0, response, 0, headBytes.Length);
        Buffer.BlockCopy(body, 0, response, headBytes.Length, body.Length);
        return response;
    }

    public static ValueTask WriteAsciiAsync(Stream stream, string text, CancellationToken cancellationToken) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text).AsMemory(), cancellationToken);
}
