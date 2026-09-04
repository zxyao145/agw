using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Projects;

public sealed class ProjectDeletionCoordinator : IProjectDeletionCoordinator
{
    private readonly AgwDbContext _dbContext;

    public ProjectDeletionCoordinator(AgwDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> ClearConversationRecordsAsync(
        ProjectConversationDeletionTarget target,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async token =>
            {
                if (!await ConversationExistsAsync(target, token).ConfigureAwait(false))
                {
                    return false;
                }

                await _dbContext
                    .ProjectConversationChatHistories.Where(history => history.ConversationId == target.ConversationId)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                await _dbContext
                    .AgentflowCheckpoints.Where(checkpoint => checkpoint.ProjectConversationId == target.ConversationId)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                await _dbContext
                    .AgentflowNodeExecutionTraces.Where(trace =>
                        trace.ProjectId == target.ProjectId && trace.ContextId == target.ContextId
                    )
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                await _dbContext
                    .TaskSessionBindings.Where(binding => binding.ProjectConversationId == target.ConversationId)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken
        );

    public Task<bool> DeleteConversationAsync(
        ProjectConversationDeletionTarget target,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async token =>
            {
                if (!await ConversationExistsAsync(target, token).ConfigureAwait(false))
                {
                    return false;
                }

                await DeleteConversationDependentsAsync([target.ConversationId], token).ConfigureAwait(false);
                await _dbContext
                    .AgentflowNodeExecutionTraces.Where(trace =>
                        trace.ProjectId == target.ProjectId && trace.ContextId == target.ContextId
                    )
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                await _dbContext
                    .ProjectConversations.Where(conversation =>
                        conversation.Id == target.ConversationId
                        && conversation.ProjectId == target.ProjectId
                        && conversation.CreateBy == target.OwnerUserId
                    )
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken
        );

    public Task<bool> DeleteAllConversationsAsync(
        ProjectDeletionTarget target,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async token =>
            {
                if (!await ProjectExistsAsync(target, token).ConfigureAwait(false))
                {
                    return false;
                }

                await DeleteAllProjectConversationsAsync(target, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken
        );

    public Task<bool> DeleteProjectAsync(ProjectDeletionTarget target, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async token =>
            {
                if (!await ProjectExistsAsync(target, token).ConfigureAwait(false))
                {
                    return false;
                }

                await DeleteJobsAsync(target, token).ConfigureAwait(false);
                await DeleteAllProjectConversationsAsync(target, token).ConfigureAwait(false);
                await _dbContext
                    .ProjectMemories.Where(memory => memory.ProjectId == target.ProjectId)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                await _dbContext
                    .ProjectSkillRelations.Where(relation => relation.ProjectId == target.ProjectId)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                await _dbContext
                    .ProjectMcpToolServers.Where(relation => relation.ProjectId == target.ProjectId)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                await _dbContext
                    .ProjectConnectionRelations.Where(relation => relation.ProjectId == target.ProjectId)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                await _dbContext
                    .Projects.Where(project => project.Id == target.ProjectId && project.CreateBy == target.OwnerUserId)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken
        );

    private async Task DeleteJobsAsync(ProjectDeletionTarget target, CancellationToken cancellationToken)
    {
        var jobIds = await _dbContext
            .Jobs.Where(job => job.ProjectId == target.ProjectId && job.CreateBy == target.OwnerUserId)
            .Select(job => job.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (jobIds.Length == 0)
        {
            return;
        }

        await _dbContext
            .JobLogs.Where(log => jobIds.Contains(log.JobId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        var deletedJobCount = await _dbContext
            .Jobs.Where(job =>
                jobIds.Contains(job.Id)
                && job.ProjectId == target.ProjectId
                && job.CreateBy == target.OwnerUserId
                && job.Status != JobStatus.Running
                && job.ActiveExecutionId == null
            )
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        if (deletedJobCount != jobIds.Length)
        {
            throw new AgwException(ErrorCodes.JobActiveAttemptConflict);
        }
    }

    private async Task DeleteAllProjectConversationsAsync(
        ProjectDeletionTarget target,
        CancellationToken cancellationToken
    )
    {
        var conversationIds = await _dbContext
            .ProjectConversations.Where(conversation =>
                conversation.ProjectId == target.ProjectId && conversation.CreateBy == target.OwnerUserId
            )
            .Select(conversation => conversation.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        await DeleteConversationDependentsAsync(conversationIds, cancellationToken).ConfigureAwait(false);
        await _dbContext
            .AgentflowNodeExecutionTraces.Where(trace => trace.ProjectId == target.ProjectId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await _dbContext
            .AgentflowCheckpoints.Where(checkpoint => checkpoint.ProjectId == target.ProjectId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await _dbContext
            .ProjectConversations.Where(conversation =>
                conversation.ProjectId == target.ProjectId && conversation.CreateBy == target.OwnerUserId
            )
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DeleteConversationDependentsAsync(
        IReadOnlyCollection<Guid> conversationIds,
        CancellationToken cancellationToken
    )
    {
        if (conversationIds.Count == 0)
        {
            return;
        }

        await _dbContext
            .ProjectConversationChatHistories.Where(history => conversationIds.Contains(history.ConversationId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await _dbContext
            .TaskSessionBindings.Where(binding => conversationIds.Contains(binding.ProjectConversationId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await _dbContext
            .AgentSessionStates.Where(session => conversationIds.Contains(session.ProjectConversationId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await _dbContext
            .AgentflowCheckpoints.Where(checkpoint => conversationIds.Contains(checkpoint.ProjectConversationId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<bool> ProjectExistsAsync(ProjectDeletionTarget target, CancellationToken cancellationToken) =>
        _dbContext
            .Projects.AsNoTracking()
            .AnyAsync(
                project => project.Id == target.ProjectId && project.CreateBy == target.OwnerUserId,
                cancellationToken
            );

    private Task<bool> ConversationExistsAsync(
        ProjectConversationDeletionTarget target,
        CancellationToken cancellationToken
    ) =>
        _dbContext
            .ProjectConversations.AsNoTracking()
            .AnyAsync(
                conversation =>
                    conversation.Id == target.ConversationId
                    && conversation.ProjectId == target.ProjectId
                    && conversation.ContextId == target.ContextId
                    && conversation.CreateBy == target.OwnerUserId
                    && _dbContext.Projects.Any(project =>
                        project.Id == conversation.ProjectId && project.CreateBy == target.OwnerUserId
                    ),
                cancellationToken
            );

    private async Task<bool> ExecuteAsync(
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await _dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = await operation(cancellationToken).ConfigureAwait(false);
        if (result)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
