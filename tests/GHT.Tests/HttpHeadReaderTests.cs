using System.Text;
using GHT.Services;
using GHT.Services.Http;
using Xunit;

namespace GHT.Tests;

public sealed class HttpHeadReaderTests
{
    [Fact]
    public async Task Reads_head_and_returns_leftover()
    {
        var bytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: a\r\n\r\nNEXT");
        using var stream = new MemoryStream(bytes);

        var result = await HttpHeadReader.ReadAsync(stream, 4096, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GET / HTTP/1.1\r\nHost: a", result!.HeadText);
        Assert.Equal("NEXT", Encoding.ASCII.GetString(result.Leftover));
    }

    [Fact]
    public async Task Rejects_bare_lf_framing()
    {
        var bytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\nHost: a\n\n");
        using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<ProxyProtocolException>(() =>
            HttpHeadReader.ReadAsync(stream, 4096, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_oversized_head()
    {
        var payload = new string('a', 200);
        var bytes = Encoding.ASCII.GetBytes($"GET /{payload} HTTP/1.1\r\nHost: a\r\n\r\n");
        using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<ProxyProtocolException>(() =>
            HttpHeadReader.ReadAsync(stream, 64, CancellationToken.None));
    }

    [Fact]
    public async Task Returns_null_on_clean_eof()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        Assert.Null(await HttpHeadReader.ReadAsync(stream, 4096, CancellationToken.None));
    }
}
