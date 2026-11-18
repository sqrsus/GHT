namespace GHT.Services.Http;

/// <summary>
/// 带内部读缓冲的流包装。
///
/// 存在的理由：chunked 请求体的「尺寸行」和「尾随头」是按行读取的，旧实现用 <c>byte[1]</c>
/// 逐字节 <c>ReadAsync</c>，每读一个字符就进一次内核 —— 大文件分块上传时这几乎是全部开销。
/// 这里一次性把数据读进 8KB 缓冲，按行扫描都在内存里完成。
///
/// 关键约束：预读到的字节不能丢。因此包装后的读取（含后续的正文拷贝）**必须全部走本对象**，
/// 不能在拿到一半时切回底层流。
/// </summary>
internal sealed class LineBufferedStream : Stream
{
    private const int DefaultBufferSize = 8 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly byte[] _buffer = new byte[DefaultBufferSize];

    private int _start;
    private int _end;

    public LineBufferedStream(Stream inner, bool leaveOpen = true)
    {
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>读一个字节；返回 -1 表示流结束。缓冲命中时只是一次数组访问。</summary>
    public async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_start < _end)
        {
            return _buffer[_start++];
        }

        if (!await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            return -1;
        }

        return _buffer[_start++];
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        if (_start < _end)
        {
            var take = Math.Min(_end - _start, destination.Length);
            _buffer.AsMemory(_start, take).CopyTo(destination);
            _start += take;
            return take;
        }

        // 大块读取直接透传，省掉一次内存拷贝（正文拷贝走的就是这条路径）
        if (destination.Length >= _buffer.Length)
        {
            return await _inner.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        if (!await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var count = Math.Min(_end - _start, destination.Length);
        _buffer.AsMemory(_start, count).CopyTo(destination);
        _start += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken).ConfigureAwait(false);

        if (read <= 0)
        {
            _start = 0;
            _end = 0;
            return false;
        }

        _start = 0;
        _end = read;
        return true;
    }
}
