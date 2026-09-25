using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Agents;

public sealed class AgentflowCheckpointPersistence : IAgentflowCheckpointPersistence
{
    private readonly AgwDbContext _dbContext;
    private readonly IDurableExecutionScopeMaintenance _scopeMaintenance;
    private readonly IConversationTurnStore _turns;

    public AgentflowCheckpointPersistence(
        AgwDbContext dbContext,
        IDurableExecutionScopeMaintenance scopeMaintenance,
        IConversationTurnStore turns
    )
    {
        _dbContext = dbContext;
        _scopeMaintenance = scopeMaintenance;
        _turns = turns;
    }

    public Task<bool> RepairAndCheckActiveExecutionsAsync(
        Guid projectId,
        Guid conversationId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    ) =>
        _scopeMaintenance.RepairAndCheckActiveExecutionsAsync(
            projectId,
            conversationId,
            ownerUserId,
            cancellationToken
        );

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<
            IAgentflowCheckpointPersistenceSession,
            CancellationToken,
            Task<AgentflowCheckpointPersistenceResult<TResult>>
        > operation,
        CancellationToken cancellationToken = default,
        Guid? conversationId = null,
        int expectedGeneration = 0
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        // 在执行写入入口的事务中调用时沿用该事务，行锁与租约检查覆盖这些写入。
        // Called inside the execution write guard's transaction, the operation joins it so the row lock and lease check cover these writes.
        await using var transaction =
            _dbContext.Database.CurrentTransaction == null
                ? await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : null;
        var result = await operation(new Session(_dbContext, _turns), cancellationToken).ConfigureAwait(false);
        if (!result.Commit)
        {
            return result.Result;
        }

        if (conversationId.HasValue)
        {
            await _dbContext.SaveConversationChangesAsync(conversationId.Value, expectedGeneration, cancellationToken);
        }
        else
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        if (transaction != null)
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result.Result;
    }

    public Task<AgentflowCheckpointRecord?> FindCheckpointAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default
    ) =>
        _dbContext
            .AgentflowCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == occurrenceId, cancellationToken);

    public Task<bool> ProjectConversationExistsAsync(
        Guid projectId,
        Guid conversationId,
        string contextId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    ) =>
        _dbContext
            .OwnedProjectConversations(projectId, ownerUserId)
            .AnyAsync(
                conversation => conversation.Id == conversationId && conversation.ContextId == contextId,
                cancellationToken
            );

    private sealed class Session : IAgentflowCheckpointPersistenceSession
    {
        private readonly AgwDbContext _dbContext;
        private readonly IConversationTurnStore _turns;

        public Session(AgwDbContext dbContext, IConversationTurnStore turns)
        {
            _dbContext = dbContext;
            _turns = turns;
        }

        public IAgentsDbContext Agents => _dbContext;

        public async Task<long> GetLastConversationSequenceAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default
        ) =>
            await _dbContext
                .ProjectConversationChatHistories.Where(history => history.ConversationId == conversationId)
                .Select(history => history.ConversationSequence)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false)
            ?? -1;

        public void AddConversationHistory(AgentflowCheckpointHistoryWrite history)
        {
            _dbContext.ProjectConversationChatHistories.Add(
                new ProjectConversationChatHistory
                {
                    Id = history.Id,
                    ConversationId = history.ConversationId,
                    TaskId = history.TaskId,
                    Status = TaskExecutionStatus.Succeeded,
                    AgentName = history.AgentName,
                    ConversationSequence = history.ConversationSequence,
                    ConversationPayload = history.ConversationPayload,
                    TurnId = history.TurnId,
                    Purpose = ConversationMessagePurpose.Message,
                    CreateTime = history.Timestamp,
                    UpdateTime = history.Timestamp,
                }
            );
        }

        public async Task DeleteConversationHistoryAfterAsync(
            Guid conversationId,
            long boundarySequence,
            CancellationToken cancellationToken = default
        )
        {
            await _dbContext
                .ProjectConversationChatHistories.Where(history =>
                    history.ConversationId == conversationId && history.ConversationSequence > boundarySequence
                )
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            await _dbContext
                .ProjectConversationTurns.Where(turn =>
                    turn.ProjectConversationId == conversationId && turn.FirstSequence > boundarySequence
                )
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            await _dbContext
                .ProjectConversationTurns.Where(turn =>
                    turn.ProjectConversationId == conversationId && turn.LastSequence > boundarySequence
                )
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(turn => turn.LastSequence, boundarySequence),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        public async Task AcceptResumeTurnAsync(
            Guid turnId,
            AgentflowCheckpointRecord checkpoint,
            CancellationToken cancellationToken = default
        )
        {
            var generation = await _dbContext
                .ProjectConversations.AsNoTracking()
                .Where(conversation => conversation.Id == checkpoint.ProjectConversationId)
                .Select(conversation => conversation.Generation)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
            await _turns
                .AcceptAsync(
                    new AcceptConversationTurnRequest
                    {
                        TurnId = turnId,
                        ProjectId = checkpoint.ProjectId,
                        ContextId = checkpoint.ContextId,
                        ConversationId = checkpoint.ProjectConversationId,
                        Generation = generation,
                        TaskId = checkpoint.TaskId,
                        TargetId = checkpoint.AgentflowId,
                        TargetType = ConversationTurnTargetType.Agentflow,
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }
}
