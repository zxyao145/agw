namespace Agw.Agents.Execution.Agents.Runtime;

/// <summary>
/// <para>委托给内层流的包装流；异步读取同时遵循调用方令牌和创建时绑定的令牌。</para>
/// <para>Stream wrapper that delegates to the inner stream; asynchronous reads observe both the caller token and the
/// token bound at creation.</para>
/// </summary>
internal sealed class CancellationBoundStream : Stream
{
    private readonly Stream _inner;
    private readonly CancellationToken _cancellationToken;

    public CancellationBoundStream(Stream inner, CancellationToken cancellationToken)
    {
        _inner = inner;
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return _inner.Read(buffer, offset, count);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // 调用方未传入令牌时直接使用绑定的令牌，避免每次读取创建关联令牌。
        // Use the bound token directly when the caller passes none, avoiding a linked source per read.
        if (!cancellationToken.CanBeCanceled)
        {
            return await _inner.ReadAsync(buffer, _cancellationToken).ConfigureAwait(false);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationToken);
        return await _inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
    }

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
