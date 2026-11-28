namespace Agw.Shared.Contracts.Coordination;

/// <summary>
/// Serializes application mutations that target the same logical resource.
/// Each resource name uses either <see cref="AcquireAsync"/> or the shared and exclusive methods.
/// 每个资源名称只能使用 <see cref="AcquireAsync"/>，或者使用共享锁与独占锁方法。
/// </summary>
public interface IApplicationLock
{
    Task<IApplicationLockLease> AcquireAsync(string resourceName, CancellationToken cancellationToken);

    /// <summary>
    /// 获取资源的共享租约。资源被独占时等待。
    /// Acquires a shared lease on the resource and waits while it is held exclusively.
    /// </summary>
    Task<IApplicationLockLease> AcquireSharedAsync(string resourceName, CancellationToken cancellationToken);

    /// <summary>
    /// 立即尝试获取独占租约。资源已有租约时返回 null。
    /// Tries to acquire an exclusive lease immediately and returns null while any lease is held.
    /// </summary>
    Task<IApplicationLockLease?> TryAcquireExclusiveAsync(string resourceName, CancellationToken cancellationToken);
}

public interface IApplicationLockLease : IAsyncDisposable
{
    CancellationToken HandleLostToken { get; }
}
