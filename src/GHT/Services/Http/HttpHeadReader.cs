using System.Text;

namespace GHT.Services.Http;

internal sealed record HeadReadResult(string HeadText, byte[] Leftover);

/// <summary>
/// 按 CRLFCRLF 边界读出一个完整的 HTTP 头，并把「多读出来的那截」原样返回。
/// 多读的那截非常关键：CONNECT 之后紧随其后的就是 TLS ClientHello，丢了就握手失败。
/// </summary>
internal static class HttpHeadReader
{
    public static async Task<HeadReadResult?> ReadAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var accumulator = new MemoryStream(4096);
        var buffer = new byte[4096];

        while (true)
        {
            var previous = accumulator.Length;
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);

            if (read <= 0)
            {
                return null;
            }

            accumulator.Write(buffer, 0, read);

            if (accumulator.Length > maxBytes)
            {
                throw new ProxyProtocolException("请求头超过大小上限");
            }

            var data = accumulator.GetBuffer();
            var length = (int)accumulator.Length;

            // 先拒绝裸 LF：只认 CRLF，否则 "\n\n" 会被当成普通内容一直读下去，
            // 最后以「请求头过大」收场，错误信息指向错误的位置。
            for (var i = Math.Max(0, previous - 1); i < length; i++)
            {
                if (data[i] == 10 && (i == 0 || data[i - 1] != 13))
                {
                    throw new ProxyProtocolException("请求头必须使用 CRLF 换行");
                }
            }

            var from = (int)Math.Max(0, previous - 3);

            for (var i = from; i + 3 < length; i++)
            {
                if (data[i] != 13 || data[i + 1] != 10 || data[i + 2] != 13 || data[i + 3] != 10)
                {
                    continue;
                }

                var headText = Encoding.ASCII.GetString(data, 0, i);
                var leftoverLength = length - (i + 4);
                var leftover = new byte[Math.Max(0, leftoverLength)];

                if (leftoverLength > 0)
                {
                    Array.Copy(data, i + 4, leftover, 0, leftoverLength);
                }

                return new HeadReadResult(headText, leftover);
            }
        }
    }
}
