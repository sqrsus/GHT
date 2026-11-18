using GHT.Services;
using GHT.Services.Http;
using Xunit;

namespace GHT.Tests;

public sealed class HttpHeadTests
{
    private static string Join(params string[] lines) => string.Join("\r\n", lines);

    [Fact]
    public void Parses_basic_request()
    {
        var head = HttpHead.Parse(Join("GET /path?q=1 HTTP/1.1", "Host: github.com", "User-Agent: test"));

        Assert.Equal("GET", head.Method);
        Assert.Equal("/path?q=1", head.Target);
        Assert.Equal("HTTP/1.1", head.Version);
        Assert.Equal("github.com", head.GetHeader("Host"));
        Assert.Null(head.ContentLength);
        Assert.False(head.IsChunked);
    }

    [Fact]
    public void Parses_content_length()
    {
        var head = HttpHead.Parse(Join("POST / HTTP/1.1", "Host: a", "Content-Length: 12"));
        Assert.Equal(12L, head.ContentLength);
    }

    [Fact]
    public void Accepts_identical_duplicate_content_length()
    {
        var head = HttpHead.Parse(Join("POST / HTTP/1.1", "Content-Length: 5", "Content-Length: 5"));
        Assert.Equal(5L, head.ContentLength);
    }

    [Fact]
    public void Rejects_conflicting_content_length()
    {
        Assert.Throws<ProxyProtocolException>(() =>
            HttpHead.Parse(Join("POST / HTTP/1.1", "Content-Length: 5", "Content-Length: 6")));
    }

    [Fact]
    public void Rejects_negative_content_length()
    {
        Assert.Throws<ProxyProtocolException>(() =>
            HttpHead.Parse(Join("POST / HTTP/1.1", "Content-Length: -1")));
    }

    [Fact]
    public void Rejects_header_without_colon()
    {
        Assert.Throws<ProxyProtocolException>(() =>
            HttpHead.Parse(Join("GET / HTTP/1.1", "BrokenHeader")));
    }

    [Fact]
    public void Rejects_folded_header()
    {
        // 以空格开头的续行（obs-fold）可被用来绕过 Host 校验
        Assert.Throws<ProxyProtocolException>(() =>
            HttpHead.Parse(Join("GET / HTTP/1.1", "Host: a", " continued")));
    }

    [Fact]
    public void Rejects_duplicate_host()
    {
        Assert.Throws<ProxyProtocolException>(() =>
            HttpHead.Parse(Join("GET / HTTP/1.1", "Host: a", "Host: b")));
    }

    [Fact]
    public void Rejects_unknown_version()
    {
        Assert.Throws<ProxyProtocolException>(() => HttpHead.Parse("GET / HTTP/2.0"));
    }

    [Fact]
    public void Rejects_request_line_without_version()
    {
        Assert.Throws<ProxyProtocolException>(() => HttpHead.Parse("GET /"));
    }

    [Fact]
    public void Rejects_bare_lf_inside_head()
    {
        Assert.Throws<ProxyProtocolException>(() =>
            HttpHead.Parse("GET / HTTP/1.1\r\nHost: a\nB: c"));
    }

    [Fact]
    public void Rejects_header_name_with_space()
    {
        Assert.Throws<ProxyProtocolException>(() =>
            HttpHead.Parse(Join("GET / HTTP/1.1", "Bad Name: x")));
    }

    [Fact]
    public void Rejects_control_char_in_value()
    {
        Assert.Throws<ProxyProtocolException>(() =>
            HttpHead.Parse("GET / HTTP/1.1\r\nX-Test: a\u0001b"));
    }

    [Fact]
    public void Detects_chunked_transfer_encoding()
    {
        var head = HttpHead.Parse(Join("POST / HTTP/1.1", "Host: a", "Transfer-Encoding: chunked"));
        Assert.True(head.IsChunked);
    }

    [Fact]
    public void Rejects_unsupported_transfer_encoding()
    {
        // 不支持 gzip 等编码：无法在不理解语义的前提下保证帧边界
        Assert.Throws<ProxyProtocolException>(() =>
            HttpHead.Parse(Join("POST / HTTP/1.1", "Transfer-Encoding: gzip")));
    }

    [Fact]
    public void Rejects_transfer_encoding_not_ending_with_chunked()
    {
        Assert.Throws<ProxyProtocolException>(() =>
            HttpHead.Parse(Join("POST / HTTP/1.1", "Transfer-Encoding: chunked, gzip")));
    }
}
