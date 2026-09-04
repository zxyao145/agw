using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Agents;

public sealed class AgentflowCheckpointPersistence : IAgentflowCheckpointPersistence
{
    private readonly AgwDbContext _dbContext;

    public AgentflowCheckpointPersistence(AgwDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<IAgentflowCheckpointPersistenceSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var transaction = await _dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = await operation(new Session(_dbContext), cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private sealed class Session : IAgentflowCheckpointPersistenceSession
    {
        private readonly AgwDbContext _dbContext;

        public Session(AgwDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public IAgentsDbContext Agents => _dbContext;

        public Task<bool> ProjectConversationExistsAsync(
            Guid projectId,
            Guid conversationId,
            string contextId,
            string ownerUserId,
            CancellationToken cancellationToken = default
        ) =>
            _dbContext
                .ProjectConversations.AsNoTracking()
                .AnyAsync(
                    conversation =>
                        conversation.Id == conversationId
                        && conversation.ProjectId == projectId
                        && conversation.ContextId == contextId
                        && conversation.CreateBy == ownerUserId
                        && _dbContext.Projects.Any(project =>
                            project.Id == conversation.ProjectId && project.CreateBy == ownerUserId
                        ),
                    cancellationToken
                );

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
