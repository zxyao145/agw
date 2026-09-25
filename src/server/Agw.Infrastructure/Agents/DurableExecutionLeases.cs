using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Infrastructure.Agents;

/// <summary>
/// 以 durable_execution 行实现租约：领取与续期是条件更新，执行写入在锁定并校验该行的短事务中提交。
/// Implements leases on the durable_execution row: claims and renewals are conditional updates, and execution writes commit in a short transaction that locks and checks the row.
/// </summary>
public sealed class DurableExecutionLeases : IDurableExecutionLeases
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public DurableExecutionLeases(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<Guid>> GetClaimableAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
            throw new AgwException(ErrorCodes.InvalidParam, "limit must be positive.");
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var now = await DatabaseClock.GetUtcNowAsync(dbContext, _timeProvider, cancellationToken).ConfigureAwait(false);
        return await dbContext
            .DurableExecutions.AsNoTracking()
            .Where(item => item.ScopeBackfilled && item.ProjectId != null && item.ProjectConversationId != null)
            .Where(item =>
                item.Status == DurableExecutionStatus.Queued
                || item.Status == DurableExecutionStatus.Resuming
                || item.Status == DurableExecutionStatus.Running && item.LeaseExpiresAt <= now
            )
            .OrderBy(item => item.Status == DurableExecutionStatus.Running ? 1 : 0)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DurableLease?> TryClaimAsync(
        Guid executionId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await using var transaction = await dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var now = await DatabaseClock.GetUtcNowAsync(dbContext, _timeProvider, cancellationToken).ConfigureAwait(false);
        var expiresAt = now + leaseDuration;
        var version = Guid.CreateVersion7();
        var claimed = await dbContext
            .DurableExecutions.Where(item => item.Id == executionId)
            .Where(item =>
                item.Status == DurableExecutionStatus.Queued
                || item.Status == DurableExecutionStatus.Resuming
                || item.Status == DurableExecutionStatus.Running && item.LeaseExpiresAt <= now
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(item => item.Status, DurableExecutionStatus.Running)
                        .SetProperty(item => item.WorkerId, workerId)
                        .SetProperty(item => item.LeaseEpoch, item => item.LeaseEpoch + 1)
                        .SetProperty(item => item.LeaseExpiresAt, expiresAt)
                        .SetProperty(item => item.StateChangedAt, now)
                        .SetProperty(item => item.StateVersion, version),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (claimed != 1)
            return null;
        var epoch = await dbContext
            .DurableExecutions.AsNoTracking()
            .Where(item => item.Id == executionId)
            .Select(item => item.LeaseEpoch)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DurableLease(executionId, workerId, epoch);
    }

    public async Task<bool> RenewAsync(DurableLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var now = await DatabaseClock.GetUtcNowAsync(dbContext, _timeProvider, cancellationToken).ConfigureAwait(false);
        var expiresAt = now + leaseDuration;
        return await Held(dbContext, lease, now)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.LeaseExpiresAt, expiresAt),
                    cancellationToken
                )
                .ConfigureAwait(false) == 1;
    }

    public IExecutionWriteGuard CreateGuard(DurableLease lease, CancellationTokenSource ownershipLost) =>
        new WriteGuard(_scopeFactory, _timeProvider, lease, ownershipLost);

    public async Task<T> RunLockedAsync<T>(
        Guid executionId,
        Func<IServiceProvider, CancellationToken, Task<T>> write,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(write);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await using var transaction = await dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var locked = await dbContext
            .DurableExecutions.Where(item => item.Id == executionId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.LeaseEpoch, item => item.LeaseEpoch),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (locked != 1)
            throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        var result = await write(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 仍由这份租约持有的可写记录：WorkerId 与 LeaseEpoch 一致、租约未到期、状态为 Running。
    /// The writable record still held by this lease: matching WorkerId and LeaseEpoch, an unexpired lease and the Running status.
    /// </summary>
    private static IQueryable<DurableExecutionRecord> Held(
        AgwDbContext dbContext,
        DurableLease lease,
        DateTimeOffset now
    ) =>
        dbContext.DurableExecutions.Where(item =>
            item.Id == lease.ExecutionId
            && item.WorkerId == lease.WorkerId
            && item.LeaseEpoch == lease.Epoch
            && item.Status == DurableExecutionStatus.Running
            && item.LeaseExpiresAt > now
        );

    private sealed class WriteGuard : IExecutionWriteGuard
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly DurableLease _lease;
        private readonly CancellationTokenSource _ownershipLost;

        public WriteGuard(
            IServiceScopeFactory scopeFactory,
            TimeProvider timeProvider,
            DurableLease lease,
            CancellationTokenSource ownershipLost
        )
        {
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider;
            _lease = lease;
            _ownershipLost = ownershipLost;
        }

        public async Task<T> RunAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<T>> write,
            CancellationToken cancellationToken
        )
        {
            ArgumentNullException.ThrowIfNull(write);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            await using var transaction = await dbContext
                .Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = await DatabaseClock
                .GetUtcNowAsync(dbContext, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            // 空更新锁定执行行：领取、释放与本次写入更新同一行，检查与提交之间不会被其他实例接管。
            // The empty update locks the execution row: claims, releases and this write update the same row, so no other instance can take over between the check and the commit.
            var locked = await Held(dbContext, _lease, now)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.LeaseEpoch, item => item.LeaseEpoch),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (locked != 1)
            {
                await _ownershipLost.CancelAsync().ConfigureAwait(false);
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "The execution lease is no longer held by this instance."
                );
            }
            var result = await write(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }
}
