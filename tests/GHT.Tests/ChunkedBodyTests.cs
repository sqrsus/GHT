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

    // ── 性能改造后的新增覆盖 ──────────────────────────────────────────────
    // 尺寸行改走带缓冲的行读取器后，预读到的正文必须不丢、不多、顺序不变。

    [Fact]
    public async Task Forwards_one_megabyte_body_verbatim()
    {
        var payload = new byte[1024 * 1024];
        new Random(7).NextBytes(payload);

        using var source = new MemoryStream();

        var chunkSize = 16 * 1024;
        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            var size = Math.Min(chunkSize, payload.Length - offset);
            var header = Encoding.ASCII.GetBytes(size.ToString("x") + "\r\n");
            source.Write(header);
            source.Write(payload, offset, size);
            source.Write(Encoding.ASCII.GetBytes("\r\n"));
        }

        source.Write(Encoding.ASCII.GetBytes("0\r\n\r\n"));
        source.Position = 0;

        using var target = new MemoryStream();
        var forwarded = await ProxyServer.CopyChunkedAsync(source, target, CancellationToken.None);

        Assert.Equal(payload.Length, forwarded);

        // 把转发结果里的分块帧还原出来，逐字节比对
        target.Position = 0;
        using var restored = new MemoryStream();
        await DecodeAsync(target, restored);

        Assert.Equal(payload, restored.ToArray());
    }

    [Fact]
    public async Task Handles_source_that_returns_tiny_reads()
    {
        // 模拟真实网络：一次只给 3 个字节。行读取器的预读行为必须依然正确。
        const string payload = "5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(payload);

        using var source = new TrickleStream(bytes, maxRead: 3);
        using var target = new MemoryStream();

        await ProxyServer.CopyChunkedAsync(source, target, CancellationToken.None);

        Assert.Equal(payload, Encoding.ASCII.GetString(target.ToArray()));
    }

    [Fact]
    public async Task Handles_long_chunk_extension()
    {
        // 尺寸行超过行读取器初始缓冲（256B），走扩容分支
        var extension = new string(';', 400);
        var payload = "4" + extension + "\r\nWiki\r\n0\r\n\r\n";

        var (forwarded, error) = await RunAsync(payload);

        Assert.Null(error);
        Assert.Equal(payload, forwarded);
    }

    [Fact]
    public async Task Accepts_uppercase_hex_size()
    {
        var (forwarded, error) = await RunAsync("A\r\n0123456789\r\n0\r\n\r\n");

        Assert.Null(error);
        Assert.Equal("A\r\n0123456789\r\n0\r\n\r\n", forwarded);
    }

    /// <summary>把 chunked 帧解码成原始字节，用于比对转发结果。</summary>
    private static async Task DecodeAsync(Stream source, Stream destination)
    {
        using var buffered = new BufferedStream(source, 256);
        var line = new StringBuilder();

        while (true)
        {
            line.Clear();
            int b;

            while ((b = buffered.ReadByte()) >= 0)
            {
                line.Append((char)b);
                if (line.Length >= 2 && line[^2] == '\r' && line[^1] == '\n')
                {
                    break;
                }
            }

            var text = line.ToString().TrimEnd('\r', '\n');
            var semicolon = text.IndexOf(';');
            if (semicolon >= 0)
            {
                text = text[..semicolon];
            }

            var size = Convert.ToInt32(text, 16);
            if (size == 0)
            {
                return;
            }

            var body = new byte[size];
            var read = 0;
            while (read < size)
            {
                var got = await buffered.ReadAsync(body.AsMemory(read, size - read));
                if (got <= 0)
                {
                    throw new InvalidOperationException("帧数据不完整");
                }

                read += got;
            }

            await destination.WriteAsync(body);

            // 跳过块尾 CRLF
            buffered.ReadByte();
            buffered.ReadByte();
        }
    }

    /// <summary>每次 Read 只返回很小一段的流，用来暴露「预读之后又丢掉」这类错误。</summary>
    private sealed class TrickleStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxRead;
        private int _offset;

        public TrickleStream(byte[] data, int maxRead)
        {
            _data = data;
            _maxRead = maxRead;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var take = Math.Min(Math.Min(_maxRead, buffer.Length), _data.Length - _offset);
            if (take <= 0)
            {
                return new ValueTask<int>(0);
            }

            _data.AsMemory(_offset, take).CopyTo(buffer);
            _offset += take;
            return new ValueTask<int>(take);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
