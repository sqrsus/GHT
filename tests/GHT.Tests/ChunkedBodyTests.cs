using System.Text;
using GHT.Services;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// chunked 请求体转发的帧校验。这些点一旦放松，就会出现「请求体截断 / 粘包 / 把数据当成分块长度」
/// 这类只在真实上传时才暴露的错误。
/// </summary>
public sealed class ChunkedBodyTests
{
    private static async Task<(string Forwarded, Exception? Error)> RunAsync(string input)
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(input));
        using var target = new MemoryStream();

        try
        {
            await ProxyServer.CopyChunkedAsync(source, target, CancellationToken.None);
            return (Encoding.ASCII.GetString(target.ToArray()), null);
        }
        catch (Exception ex)
        {
            return (Encoding.ASCII.GetString(target.ToArray()), ex);
        }
    }

    [Fact]
    public async Task Forwards_valid_body_verbatim()
    {
        const string payload = "4\r\nWiki\r\n5\r\npedia\r\n0\r\n\r\n";

        var (forwarded, error) = await RunAsync(payload);

        Assert.Null(error);
        Assert.Equal(payload, forwarded);
    }

    [Fact]
    public async Task Keeps_chunk_extension()
    {
        const string payload = "4;name=value\r\nWiki\r\n0\r\n\r\n";

        var (forwarded, error) = await RunAsync(payload);

        Assert.Null(error);
        Assert.Equal(payload, forwarded);
    }

    [Fact]
    public async Task Forwards_trailers()
    {
        const string payload = "0\r\nX-Checksum: abc\r\n\r\n";

        var (forwarded, error) = await RunAsync(payload);

        Assert.Null(error);
        Assert.Equal(payload, forwarded);
    }

    [Fact]
    public async Task Accepts_binary_payload_containing_zero_bytes()
    {
        // 声明 4 字节，内容为 0x00 0x01 0x00 0x02
        var head = Encoding.ASCII.GetBytes("4\r\n");
        var body = new byte[] { 0x00, 0x01, 0x00, 0x02 };
        var tail = Encoding.ASCII.GetBytes("\r\n0\r\n\r\n");

        using var source = new MemoryStream();
        source.Write(head);
        source.Write(body);
        source.Write(tail);
        source.Position = 0;

        using var target = new MemoryStream();
        await ProxyServer.CopyChunkedAsync(source, target, CancellationToken.None);

        Assert.Equal(head.Length + body.Length + tail.Length, (int)target.Length);
    }

    [Fact]
    public async Task Rejects_bare_lf_size_line()
    {
        var (_, error) = await RunAsync("4\nWiki\r\n0\r\n\r\n");
        Assert.IsType<ProxyProtocolException>(error);
    }

    [Fact]
    public async Task Rejects_missing_crlf_after_data()
    {
        var (_, error) = await RunAsync("4\r\nWikiXX0\r\n\r\n");
        Assert.IsType<ProxyProtocolException>(error);
    }

    [Fact]
    public async Task Rejects_invalid_hex_size()
    {
        var (_, error) = await RunAsync("zz\r\nWiki\r\n0\r\n\r\n");
        Assert.IsType<ProxyProtocolException>(error);
    }

    [Fact]
    public async Task Rejects_oversized_chunk_declaration()
    {
        // 0x7FFFFFFF ≈ 2GB：声明合法十六进制，但远超单块上限，必须在读数据前拒绝
        var (_, error) = await RunAsync("7FFFFFFF\r\n");
        Assert.IsType<ProxyProtocolException>(error);
    }

    [Fact]
    public async Task Rejects_truncated_body()
    {
        var (_, error) = await RunAsync("10\r\nWiki");
        Assert.IsType<ProxyProtocolException>(error);
    }

    [Fact]
    public async Task Rejects_missing_terminating_chunk()
    {
        var (_, error) = await RunAsync("4\r\nWiki\r\n");
        Assert.IsType<ProxyProtocolException>(error);
    }
}
