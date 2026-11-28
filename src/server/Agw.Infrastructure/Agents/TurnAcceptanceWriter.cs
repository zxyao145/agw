using System.Data;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Messages;
using Agw.Auth.Contracts;
using Agw.Extensions.Contracts;
using Agw.Infrastructure.Data;
using Agw.Projects.Contracts;
using Agw.Projects.Contracts.History;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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
        var scopedServices = new ScopedAcceptanceServices
        {
            DbContext = dbContext,
            Turns = turns,
            SnapshotReader = scope.ServiceProvider.GetRequiredService<IAgentflowRevisionSnapshotReader>(),
            ServiceProvider = scope.ServiceProvider,
        };
        await using var definitionLease =
            turn.TargetType == ConversationTurnTargetType.Agentflow
                ? await AgentDefinitionLock
                    .AcquireScopeAsync(_applicationLock, UserInfoUtil.RequiredUserId, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        var transactionToken = definitionLease?.Token ?? cancellationToken;
        return await AcceptInScopeAsync(write, scopedServices, transactionToken, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TurnAcceptanceResult> AcceptInScopeAsync(
        TurnAcceptanceWrite write,
        ScopedAcceptanceServices scopedServices,
        CancellationToken transactionToken,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await AcceptTransactionAsync(write, scopedServices, transactionToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException
                    is not PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }
            )
        {
            // 主键冲突说明另一个受理先提交了这个 turnId；只有同一用户、同一对话的记录可见并匹配。
            // A key conflict means another acceptance committed this turnId first; only a record of the same user and conversation is visible and matches.
            var dbContext = scopedServices.DbContext;
            var turns = scopedServices.Turns;
            var turn = write.Turn;
            dbContext.ChangeTracker.Clear();
            var existing = await turns.GetAsync(turn.TurnId, cancellationToken).ConfigureAwait(false);
            if (existing == null || existing.ConversationId != turn.ConversationId)
                throw new AgwException(ErrorCodes.ResourceNotFound);
            return new TurnAcceptanceResult(
                existing,
                Created: false,
                await LoadDurableIfExistsAsync(dbContext, turn.TurnId, cancellationToken).ConfigureAwait(false)
            );
        }
    }

    private async Task<TurnAcceptanceResult> AcceptTransactionAsync(
        TurnAcceptanceWrite write,
        ScopedAcceptanceServices scopedServices,
        CancellationToken transactionToken
    )
    {
        var turn = write.Turn;
        var dbContext = scopedServices.DbContext;
        var turns = scopedServices.Turns;
        await using var transaction =
            turn.TargetType == ConversationTurnTargetType.Agentflow
                ? await dbContext
                    .Database.BeginTransactionAsync(IsolationLevel.Serializable, transactionToken)
                    .ConfigureAwait(false)
                : await dbContext.Database.BeginTransactionAsync(transactionToken).ConfigureAwait(false);
        var existing = await turns.GetAsync(turn.TurnId, transactionToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.ConversationId != turn.ConversationId)
                throw new AgwException(ErrorCodes.ResourceNotFound);
            var existingDurable = await LoadDurableIfExistsAsync(dbContext, turn.TurnId, transactionToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(transactionToken).ConfigureAwait(false);
            return new TurnAcceptanceResult(existing, Created: false, existingDurable);
        }

        var result = await AcceptNewTurnAsync(write, scopedServices, transactionToken).ConfigureAwait(false);
        await transaction.CommitAsync(transactionToken).ConfigureAwait(false);
        return result;
    }

    private async Task<TurnAcceptanceResult> AcceptNewTurnAsync(
        TurnAcceptanceWrite write,
        ScopedAcceptanceServices scopedServices,
        CancellationToken cancellationToken
    )
    {
        var turn = write.Turn;
        var snapshot = await ResolveSnapshotAsync(write, scopedServices.SnapshotReader, cancellationToken)
            .ConfigureAwait(false);
        var acceptedTurn = WithSnapshot(turn, snapshot);
        var registration = await write.Register(snapshot, cancellationToken).ConfigureAwait(false);
        if (registration.ExtensionIds.Count > 0)
        {
            await scopedServices
                .ServiceProvider.GetRequiredService<IExtensionAdmissionValidator>()
                .ValidateEnabledAsync(registration.ExtensionIds, cancellationToken)
                .ConfigureAwait(false);
        }

        var acceptance = await scopedServices.Turns.AcceptAsync(acceptedTurn, cancellationToken).ConfigureAwait(false);
        var durable = await RegisterDurableAsync(
                scopedServices.DbContext,
                turn,
                acceptedTurn,
                registration.Durable,
                acceptance.Created,
                cancellationToken
            )
            .ConfigureAwait(false);
        return new TurnAcceptanceResult(acceptance.Turn, acceptance.Created, durable);
    }

    private static async Task<AgentflowRevisionSnapshot?> ResolveSnapshotAsync(
        TurnAcceptanceWrite write,
        IAgentflowRevisionSnapshotReader snapshotReader,
        CancellationToken cancellationToken
    )
    {
        if (write.Turn.TargetType != ConversationTurnTargetType.Agentflow)
        {
            return null;
        }
        if (write.PinnedSnapshot is { } pinnedSnapshot)
        {
            return pinnedSnapshot;
        }

        return await snapshotReader
            .CreateRevisionSnapshotAsync(write.Turn.TargetId, write.RequestedAgentflowRevision, cancellationToken)
            .ConfigureAwait(false);
    }

    private static AcceptConversationTurnRequest WithSnapshot(
        AcceptConversationTurnRequest turn,
        AgentflowRevisionSnapshot? snapshot
    ) =>
        snapshot is null
            ? turn
            : turn with
            {
                AgentflowRevision = snapshot.RootRevision,
                AgentflowDependencies = snapshot
                    .Dependencies.Select(AgentflowDependencyRevisionMapping.ToDto)
                    .ToArray(),
            };

    private async Task<DurableTurnAcceptance?> RegisterDurableAsync(
        AgwDbContext dbContext,
        AcceptConversationTurnRequest turn,
        AcceptConversationTurnRequest acceptedTurn,
        DurableTurnRegistration? registration,
        bool acceptanceCreated,
        CancellationToken cancellationToken
    ) =>
        registration switch
        {
            null => null,
            { } durableRegistration when acceptanceCreated => await RegisterAsync(
                    dbContext,
                    acceptedTurn,
                    durableRegistration,
                    cancellationToken
                )
                .ConfigureAwait(false),
            _ => await LoadDurableIfExistsAsync(dbContext, turn.TurnId, cancellationToken).ConfigureAwait(false),
        };

    private sealed class ScopedAcceptanceServices
    {
        public required AgwDbContext DbContext { get; init; }
        public required IConversationTurnStore Turns { get; init; }
        public required IAgentflowRevisionSnapshotReader SnapshotReader { get; init; }
        public required IServiceProvider ServiceProvider { get; init; }
    }

    private async Task<DurableTurnAcceptance> RegisterAsync(
        AgwDbContext dbContext,
        AcceptConversationTurnRequest turn,
        DurableTurnRegistration registration,
        CancellationToken cancellationToken
    )
    {
        var now = await DatabaseClock.GetUtcNowAsync(dbContext, _timeProvider, cancellationToken).ConfigureAwait(false);
        var record = CreateDurableExecutionRecord(turn, registration, now);
        dbContext.DurableExecutions.Add(record);
        dbContext.DurableExecutionEvents.Add(CreateStartEvent(turn, registration, record));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var local = !string.IsNullOrWhiteSpace(registration.WorkerId);
        return new DurableTurnAcceptance(
            record.Status,
            local ? new DurableLease(record.Id, record.WorkerId!, record.LeaseEpoch) : null
        );
    }

    private static DurableExecutionRecord CreateDurableExecutionRecord(
        AcceptConversationTurnRequest turn,
        DurableTurnRegistration registration,
        DateTimeOffset now
    )
    {
        var local = !string.IsNullOrWhiteSpace(registration.WorkerId);
        return new DurableExecutionRecord
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
    }

    private static DurableExecutionEventRecord CreateStartEvent(
        AcceptConversationTurnRequest turn,
        DurableTurnRegistration registration,
        DurableExecutionRecord record
    ) =>
        new()
        {
            Id = registration.StartEventId,
            TurnId = turn.TurnId,
            TurnSequence = 1,
            LeaseEpoch = record.LeaseEpoch,
            SegmentIndex = 0,
            MessageType = AgwMessageTypes.TurnStart,
            PayloadJson = registration.StartEventPayloadJson,
            CreateBy = registration.UserId,
            UpdateBy = registration.UserId,
        };

    private static async Task<DurableTurnAcceptance?> LoadDurableIfExistsAsync(
        AgwDbContext dbContext,
        Guid turnId,
        CancellationToken cancellationToken
    )
    {
        var record = await dbContext
            .DurableExecutions.AsNoTracking()
            .Where(item => item.Id == turnId)
            .Select(item => new { item.Status })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }
        // 重发只返回当前状态；租约属于最初受理的执行。
        // A resend only returns the current status; the lease belongs to the original execution.
        return new DurableTurnAcceptance(record.Status, Lease: null);
    }
}
