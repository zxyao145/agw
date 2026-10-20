namespace Agw.Files.Tests;

/// <summary>
/// 写入测试的内容流：按固定大小分块读出内容，每读出一块后执行回调，参数为从 0 开始的块序号与是否为最后一块。回调可以
/// 抛出异常、取消、修改目标文件或等待，用来在写入的指定阶段制造外部变化。
/// A content stream for write tests: it reads its content in fixed-size blocks and runs a callback after each block
/// with the 0-based block index and whether it is the last block. The callback may throw, cancel, modify the target
/// file or wait, creating external changes at a chosen stage of a write.
/// </summary>
internal sealed class BlockContentStream : Stream
{
    private readonly byte[] _content;
    private readonly int _blockSize;
    private readonly Func<int, bool, CancellationToken, Task> _onBlockRead;
    private int _position;
    private int _blockIndex;

    public BlockContentStream(byte[] content, int blockSize, Func<int, bool, CancellationToken, Task> onBlockRead)
    {
        _content = content;
        _blockSize = blockSize;
        _onBlockRead = onBlockRead;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = Math.Min(Math.Min(buffer.Length, _blockSize), _content.Length - _position);
        if (count == 0)
        {
            return 0;
        }

        _content.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        await _onBlockRead(_blockIndex++, _position == _content.Length, cancellationToken);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("BlockContentStream supports asynchronous reads only.");

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
