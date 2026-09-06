using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Agents;

public sealed class AgentflowCheckpointPersistence : IAgentflowCheckpointPersistence
{
    private readonly AgwDbContext _dbContext;
    private readonly IDurableExecutionScopeMaintenance _scopeMaintenance;

    public AgentflowCheckpointPersistence(AgwDbContext dbContext, IDurableExecutionScopeMaintenance scopeMaintenance)
    {
        _dbContext = dbContext;
        _scopeMaintenance = scopeMaintenance;
    }

    public async Task BackfillExecutionScopesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _scopeMaintenance.BackfillAsync(cancellationToken).ConfigureAwait(false);
        if (result.HasPending)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "Execution scope recovery is still pending. Retry after recovery completes."
            );
        }
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

        await using var transaction = await _dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = await operation(new Session(_dbContext), cancellationToken).ConfigureAwait(false);
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

        public Session(AgwDbContext dbContext)
        {
            _dbContext = dbContext;
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
                    CreateTime = history.Timestamp,
                    UpdateTime = history.Timestamp,
                }
            );
        }

        public Task DeleteConversationHistoryAfterAsync(
            Guid conversationId,
            long boundarySequence,
            CancellationToken cancellationToken = default
        ) =>
            _dbContext
                .ProjectConversationChatHistories.Where(history =>
                    history.ConversationId == conversationId && history.ConversationSequence > boundarySequence
                )
                .ExecuteDeleteAsync(cancellationToken);
    }
}
