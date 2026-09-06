using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Projects;

public sealed class ProjectDeletionCoordinator : IProjectDeletionCoordinator
{
    private readonly AgwDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;
    private readonly IDurableExecutionScopeMaintenance _scopeMaintenance;

    public ProjectDeletionCoordinator(
        AgwDbContext dbContext,
        IApplicationLock applicationLock,
        IDurableExecutionScopeMaintenance scopeMaintenance
    )
    {
        _dbContext = dbContext;
        _applicationLock = applicationLock;
        _scopeMaintenance = scopeMaintenance;
    }

    public async Task<bool> ClearConversationRecordsAsync(
        ProjectConversationDeletionTarget target,
        CancellationToken cancellationToken = default
    )
    {
        var generation = await _dbContext
            .OwnedProjectConversations(target.ProjectId, target.OwnerUserId)
            .Where(conversation =>
                conversation.Id == target.ConversationId && conversation.ContextId == target.ContextId
            )
            .Select(conversation => (int?)conversation.Generation)
            .SingleOrDefaultAsync(cancellationToken);
        if (generation == null)
            return false;
        var gate = new ConversationExecutionGate(_dbContext, _applicationLock, TimeProvider.System);
        await using var resetLease = await gate.AcquireAsync(
            target.ConversationId,
            generation.Value,
            cancellationToken
        );
        using var resetCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            resetLease.HandleLostToken
        );
        return await ExecuteProjectAsync(
            target.ProjectId,
            target.OwnerUserId,
            async token =>
            {
                if (!await ConversationExistsAsync(target, token).ConfigureAwait(false))
                    return false;
                if (
                    await _scopeMaintenance.RepairAndCheckActiveExecutionsAsync(
                        target.ProjectId,
                        target.ConversationId,
                        target.OwnerUserId,
                        token
                    )
                )
                {
                    throw new AgwException(ErrorCodes.ConversationSessionConflict);
                }
                var changed = await _dbContext
                    .OwnedProjectConversations(target.ProjectId, target.OwnerUserId)
                    .Where(conversation =>
                        conversation.Id == target.ConversationId
                        && conversation.Generation == generation.Value
                        && conversation.Generation < int.MaxValue
                    )
                    .ExecuteUpdateAsync(
                        setters =>
                            setters.SetProperty(
                                conversation => conversation.Generation,
                                conversation => conversation.Generation + 1
                            ),
                        token
                    );
                if (changed != 1)
                    throw new AgwException(ErrorCodes.ConversationSessionConflict);
                await DeleteConversationDependentsAsync([target.ConversationId], target.OwnerUserId, token);
                await _dbContext
                    .AgentflowNodeExecutionTraces.Where(trace =>
                        trace.ProjectId == target.ProjectId && trace.ContextId == target.ContextId
                    )
                    .ExecuteDeleteAsync(token);
                return true;
            },
            resetCancellation.Token
        );
    }

    public Task<bool> DeleteConversationAsync(
        ProjectConversationDeletionTarget target,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteProjectAsync(
            target.ProjectId,
            target.OwnerUserId,
            async token =>
            {
                if (!await ConversationExistsAsync(target, token).ConfigureAwait(false))
                {
                    return false;
                }

                await DeleteDurableExecutionsAsync(target.ProjectId, target.ConversationId, target.OwnerUserId, token)
                    .ConfigureAwait(false);
                await DeleteConversationDependentsAsync([target.ConversationId], target.OwnerUserId, token)
                    .ConfigureAwait(false);
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
        ExecuteProjectAsync(
            target.ProjectId,
            target.OwnerUserId,
            async token =>
            {
                if (!await ProjectExistsAsync(target, token).ConfigureAwait(false))
                {
                    return false;
                }

                await DeleteDurableExecutionsAsync(target.ProjectId, null, target.OwnerUserId, token)
                    .ConfigureAwait(false);
                await DeleteAllProjectConversationsAsync(target, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken
        );

    public Task<bool> DeleteProjectAsync(ProjectDeletionTarget target, CancellationToken cancellationToken = default) =>
        ExecuteProjectAsync(
            target.ProjectId,
            target.OwnerUserId,
            async token =>
            {
                if (
                    !await _dbContext
                        .LockOwnedProjectAsync(target.ProjectId, target.OwnerUserId, token)
                        .ConfigureAwait(false)
                )
                {
                    return false;
                }

                await DeleteDurableExecutionsAsync(target.ProjectId, null, target.OwnerUserId, token)
                    .ConfigureAwait(false);
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
        await DeleteConversationDependentsAsync(conversationIds, target.OwnerUserId, cancellationToken)
            .ConfigureAwait(false);
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
        string ownerUserId,
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
            .AgentSessionStates.IgnoreUserScope()
            .Where(session =>
                conversationIds.Contains(session.ProjectConversationId)
                && _dbContext.ProjectConversations.Any(conversation =>
                    conversation.Id == session.ProjectConversationId
                    && conversation.CreateBy == ownerUserId
                    && conversation.Project!.CreateBy == ownerUserId
                )
            )
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
            .OwnedProjectConversations(target.ProjectId, target.OwnerUserId)
            .AnyAsync(
                conversation => conversation.Id == target.ConversationId && conversation.ContextId == target.ContextId,
                cancellationToken
            );

    private async Task DeleteDurableExecutionsAsync(
        Guid projectId,
        Guid? conversationId,
        string ownerUserId,
        CancellationToken cancellationToken
    )
    {
        var executions = _dbContext.DurableExecutions.Where(execution =>
            execution.UserId == ownerUserId
            && execution.ProjectId == projectId
            && (!conversationId.HasValue || execution.ProjectConversationId == conversationId.Value)
        );
        var executionIds = executions.Select(execution => execution.Id);
        await _dbContext
            .DurableExecutionEvents.Where(entry => executionIds.Contains(entry.ExecutionId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await executions.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ExecuteProjectAsync(
        Guid projectId,
        string ownerUserId,
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken
    )
    {
        await using var lifecycleLease = await _applicationLock
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(projectId), cancellationToken)
            .ConfigureAwait(false);
        using var mutation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifecycleLease.HandleLostToken
        );
        cancellationToken = mutation.Token;
        var backfill = await _scopeMaintenance.BackfillAsync(cancellationToken).ConfigureAwait(false);
        if (backfill.HasPending)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "Execution scope recovery is still pending. Retry after recovery completes."
            );
        }
        return await ExecuteAsync(
                async token =>
                {
                    if (!await _dbContext.LockOwnedProjectAsync(projectId, ownerUserId, token))
                        return false;
                    return await operation(token);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

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
