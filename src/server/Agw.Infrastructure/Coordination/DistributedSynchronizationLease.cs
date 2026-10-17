using System.Data.Common;
using Agw.Shared.Contracts.Coordination;
using Medallion.Threading;

namespace Agw.Infrastructure.Coordination;

internal sealed class DistributedSynchronizationLease : IApplicationLockLease
{
    private readonly IDistributedSynchronizationHandle _handle;

    public DistributedSynchronizationLease(IDistributedSynchronizationHandle handle)
    {
        _handle = handle;
    }

    public CancellationToken HandleLostToken => _handle.HandleLostToken;

    public async ValueTask DisposeAsync()
    {
        var lost = _handle.HandleLostToken.IsCancellationRequested;
        try
        {
            await _handle.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (lost && exception is DbException or InvalidOperationException)
        {
            // 连接中断后，释放锁可能报错，数据库已处理该锁。
            // A lost connection can make disposal fail after the database has released the lock.
        }
    }
}
