using Agw.Shared.Contracts.Coordination;

namespace Agw.Agents.Application.Persistence;

/// <summary>
/// Durable 执行的租约：领取、续期与执行写入入口。时间一律取数据库当前时间。
/// Durable execution leases: claiming, renewal and the execution write entry. Time always comes from the database clock.
/// </summary>
public interface IDurableExecutionLeases
{
    /// <summary>
    /// 可领取的记录：Queued、Resuming，以及租约已经到期的 Running。
    /// Claimable records: Queued, Resuming, and Running whose lease has expired.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetClaimableAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// 条件更新领取一条记录：写入本实例的 WorkerId、LeaseEpoch 加一与新的到期时间；其他实例先领取时返回空。
    /// Claims one record with a conditional update: writes this instance's WorkerId, increments LeaseEpoch and sets a new expiry; returns null when another instance claimed first.
    /// </summary>
    Task<DurableLease?> TryClaimAsync(
        Guid executionId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 以 WorkerId + LeaseEpoch 与仍然有效的租约为条件续期；返回假表示租约已经失去。
    /// Renews on the condition of WorkerId + LeaseEpoch and a still valid lease; false means the lease is lost.
    /// </summary>
    Task<bool> RenewAsync(DurableLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>
    /// 创建这份租约的执行写入入口；检查失败时取消 ownershipLost。
    /// Creates the execution write entry of this lease; a failed check cancels ownershipLost.
    /// </summary>
    IExecutionWriteGuard CreateGuard(DurableLease lease, CancellationTokenSource ownershipLost);

    /// <summary>
    /// 用户命令的事务：锁定执行行后写入，不要求持有租约；与租约检查事务互斥，因此事件序号保持连续。
    /// The transaction of a user command: writes after locking the execution row without requiring a lease; it excludes lease-checked transactions so event sequences stay contiguous.
    /// </summary>
    Task<T> RunLockedAsync<T>(
        Guid executionId,
        Func<IServiceProvider, CancellationToken, Task<T>> write,
        CancellationToken cancellationToken
    );
}
