using Agw.Shared.Contracts.Coordination;

namespace Agw.Files.Infrastructure.Storage;

/// <summary>
/// 由 <see cref="FilePathLocks"/> 取得的文件写锁，释放时按获取的相反顺序归还租约。Distributed 模式下租约可能丢失（例如
/// 数据库连接中断），丢失只是通知，原来的持有者仍可能在执行。取得锁时把调用方的取消令牌与每个租约的
/// <see cref="IApplicationLockLease.HandleLostToken"/> 链接为 <see cref="Token"/>：分布式锁在第一次读取 HandleLostToken
/// 时才开始监视连接，因此在取得锁时读取；持有锁期间的复制使用这个令牌，租约丢失时随之中止，提交前调用
/// <see cref="ThrowIfLost"/> 拒绝这样的持有者。
/// A file write lock acquired by <see cref="FilePathLocks"/>; disposing it returns the leases in reverse acquisition
/// order. In Distributed mode a lease can be lost (for example when the database connection drops); the loss is only a
/// notification and the former holder may still be running. On acquisition the caller's cancellation token and the
/// <see cref="IApplicationLockLease.HandleLostToken"/> of every lease are linked into <see cref="Token"/>: the
/// distributed lock starts monitoring its connection only on the first read of HandleLostToken, so it is read at
/// acquisition. Copying while the lock is held uses this token and aborts when a lease is lost, and
/// <see cref="ThrowIfLost"/> is called before committing to reject such a holder.
/// </summary>
public sealed class FilePathLease : IAsyncDisposable
{
    private readonly IApplicationLockLease[] _leases;
    private readonly CancellationTokenSource _cancellation;
    private int _disposed;

    internal FilePathLease(IApplicationLockLease[] leases, CancellationToken ct)
    {
        _leases = leases;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource([
            ct,
            .. leases.Select(lease => lease.HandleLostToken),
        ]);
    }

    /// <summary>
    /// 调用方取消或任一租约丢失时取消的令牌，供持有锁期间的 IO 使用。
    /// A token canceled when the caller cancels or any lease is lost, used by IO while the lock is held.
    /// </summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>
    /// 调用方已取消或任一租约已丢失时抛出 <see cref="OperationCanceledException"/>。
    /// Throws <see cref="OperationCanceledException"/> when the caller has canceled or any lease has been lost.
    /// </summary>
    public void ThrowIfLost() => _cancellation.Token.ThrowIfCancellationRequested();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Dispose();
        await DisposeFromAsync(_leases.Length - 1).ConfigureAwait(false);
    }

    /// <summary>
    /// 从 <paramref name="index"/> 起向前依次归还租约；前一个租约释放失败时，后面的租约仍在 finally 中归还。
    /// Returns the leases from <paramref name="index"/> backwards; when releasing one lease fails, the remaining leases
    /// are still returned in the finally block.
    /// </summary>
    private async ValueTask DisposeFromAsync(int index)
    {
        if (index < 0)
        {
            return;
        }

        try
        {
            await _leases[index].DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await DisposeFromAsync(index - 1).ConfigureAwait(false);
        }
    }
}
