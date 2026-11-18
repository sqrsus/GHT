namespace GHT.Services.Http;

/// <summary>
/// 只读的「缓冲区前置流」：先吐出读头时多读的那几个字节，再转发底层流的后续内容。
/// 这样隧道/请求体转发可以直接用标准 <see cref="Stream"/> API。
/// </summary>
internal sealed class BufferedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _prefix;
    private int _offset;

    public BufferedReadStream(Stream inner, byte[] prefix)
    {
        _inner = inner;
        _prefix = prefix;
        _offset = 0;
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

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < _prefix.Length)
        {
            var available = _prefix.Length - _offset;
            var take = Math.Min(available, buffer.Length);

            if (take > 0)
            {
                _prefix.AsMemory(_offset, take).CopyTo(buffer);
                _offset += take;
                return new ValueTask<int>(take);
            }
        }

        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
