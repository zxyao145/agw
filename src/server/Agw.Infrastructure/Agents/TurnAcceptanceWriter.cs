using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Projects.Contracts;
using Agw.Projects.Contracts.History;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Infrastructure.Agents;

/// <summary>
/// 受理事务的实现：在项目生命周期锁与对话历史锁之后，用同一个 AgwDbContext 事务写入 Projects 的输入行与 Turn 行，以及 Agents 的执行记录与开始事件。
/// 每次受理使用独立的作用域：连接在整个生命周期内复用调用方的作用域，已跟踪的实体不能影响下一次受理。
/// The acceptance transaction: after the project lifecycle and conversation history locks, one AgwDbContext transaction writes the Projects input and turn rows and the Agents execution record and start event.
/// Every acceptance uses its own scope: a connection keeps the caller's scope for its whole lifetime, and entities it tracked must not affect the next acceptance.
/// </summary>
public sealed class TurnAcceptanceWriter : ITurnAcceptanceWriter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IApplicationLock _applicationLock;
    private readonly TimeProvider _timeProvider;

    public TurnAcceptanceWriter(
        IServiceScopeFactory scopeFactory,
        IApplicationLock applicationLock,
        TimeProvider timeProvider
    )
    {
        _scopeFactory = scopeFactory;
        _applicationLock = applicationLock;
        _timeProvider = timeProvider;
    }

    public async Task<TurnAcceptanceResult> AcceptAsync(TurnAcceptanceWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var turn = write.Turn;
        var contextId = ContextIdUtil.NormalizeContextId(turn.ContextId);
        await using var lifecycleLease = await _applicationLock
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(turn.ProjectId), cancellationToken)
            .ConfigureAwait(false);
        await using var historyLease = await _applicationLock
            .AcquireAsync(ConversationHistoryLock.GetResourceName(turn.ProjectId, contextId), cancellationToken)
            .ConfigureAwait(false);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var turns = scope.ServiceProvider.GetRequiredService<IConversationTurnStore>();
        try
        {
            await using var transaction = await dbContext
                .Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var acceptance = await turns.AcceptAsync(turn, cancellationToken).ConfigureAwait(false);
            var durable = write.Durable switch
            {
                null => null,
                { } registration when acceptance.Created => await RegisterAsync(
                        dbContext,
                        turn,
                        registration,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                _ => await LoadDurableAsync(dbContext, turn.TurnId, cancellationToken).ConfigureAwait(false),
            };
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new TurnAcceptanceResult(acceptance.Turn, acceptance.Created, durable);
        }
        catch (DbUpdateException)
        {
            // 主键冲突说明另一个受理先提交了这个 turnId；只有同一用户、同一对话的记录可见并匹配。
            // A key conflict means another acceptance committed this turnId first; only a record of the same user and conversation is visible and matches.
            dbContext.ChangeTracker.Clear();
            var existing = await turns.GetAsync(turn.TurnId, cancellationToken).ConfigureAwait(false);
            if (existing == null || existing.ConversationId != turn.ConversationId)
                throw new AgwException(ErrorCodes.ResourceNotFound);
            return new TurnAcceptanceResult(
                existing,
                Created: false,
                write.Durable == null
                    ? null
                    : await LoadDurableAsync(dbContext, turn.TurnId, cancellationToken).ConfigureAwait(false)
            );
        }
    }

    private async Task<DurableTurnAcceptance> RegisterAsync(
        AgwDbContext dbContext,
        AcceptConversationTurnRequest turn,
        DurableTurnRegistration registration,
        CancellationToken cancellationToken
    )
    {
        var now = await DatabaseClock.GetUtcNowAsync(dbContext, _timeProvider, cancellationToken).ConfigureAwait(false);
        var local = !string.IsNullOrWhiteSpace(registration.WorkerId);
        var record = new DurableExecutionRecord
        {
            Id = turn.TurnId,
            UserId = registration.UserId,
            CreateBy = registration.UserId,
            UpdateBy = registration.UserId,
            ProjectId = turn.ProjectId,
            ProjectConversationId = turn.ConversationId,
            ScopeBackfilled = true,
            ManifestJson = registration.ManifestJson,
            Status = local ? DurableExecutionStatus.Running : DurableExecutionStatus.Queued,
            SegmentIndex = 0,
            StateChangedAt = now,
            StateVersion = Guid.CreateVersion7(),
            WorkerId = local ? registration.WorkerId : null,
            LeaseEpoch = local ? 1 : 0,
            LeaseExpiresAt = local ? now + registration.LeaseDuration : null,
            LastEventSequence = 1,
        };
        dbContext.DurableExecutions.Add(record);
        dbContext.DurableExecutionEvents.Add(
            new DurableExecutionEventRecord
            {
                Id = registration.StartEventId,
                TurnId = turn.TurnId,
                TurnSequence = 1,
                LeaseEpoch = record.LeaseEpoch,
                SegmentIndex = 0,
                PayloadJson = registration.StartEventPayloadJson,
                CreateBy = registration.UserId,
                UpdateBy = registration.UserId,
            }
        );
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DurableTurnAcceptance(
            record.Status,
            local ? new DurableLease(record.Id, record.WorkerId!, record.LeaseEpoch) : null
        );
    }

    private static async Task<DurableTurnAcceptance> LoadDurableAsync(
        AgwDbContext dbContext,
        Guid turnId,
        CancellationToken cancellationToken
    )
    {
        var record =
            await dbContext
                .DurableExecutions.AsNoTracking()
                .Where(item => item.Id == turnId)
                .Select(item => new { item.Status })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        // 重发只返回当前状态；租约属于最初受理的执行。
        // A resend only returns the current status; the lease belongs to the original execution.
        return new DurableTurnAcceptance(record.Status, Lease: null);
    }
}
