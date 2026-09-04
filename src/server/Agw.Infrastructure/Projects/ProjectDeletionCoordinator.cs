using System.Text.Json;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Infrastructure.Projects;

public sealed class ProjectDeletionCoordinator : IProjectDeletionCoordinator
{
    private readonly AgwDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;
    private readonly ILogger<ProjectDeletionCoordinator> _logger;

    public ProjectDeletionCoordinator(
        AgwDbContext dbContext,
        IApplicationLock? applicationLock = null,
        ILogger<ProjectDeletionCoordinator>? logger = null
    )
    {
        _dbContext = dbContext;
        _applicationLock = applicationLock ?? InMemoryApplicationLock.Shared;
        _logger = logger ?? NullLogger<ProjectDeletionCoordinator>.Instance;
    }

    public Task<bool> ClearConversationRecordsAsync(
        ProjectConversationDeletionTarget target,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteProjectAsync(
            target.ProjectId,
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
        ExecuteProjectAsync(
            target.ProjectId,
            async token =>
            {
                if (!await ConversationExistsAsync(target, token).ConfigureAwait(false))
                {
                    return false;
                }

                await DeleteDurableExecutionsAsync(target.ProjectId, target.ConversationId, target.OwnerUserId, token)
                    .ConfigureAwait(false);
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
        ExecuteProjectAsync(
            target.ProjectId,
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
        // ManifestJson is decrypted by the entity materialization interceptor; a projection would retain ciphertext.
        var candidates = await _dbContext
            .DurableExecutions.AsNoTracking()
            .Where(execution => execution.UserId == ownerUserId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var executionIds = new List<Guid>();
        foreach (var candidate in candidates)
        {
            if (!TryGetDurableExecutionScope(candidate.ManifestJson, out var scope))
            {
                _logger.LogWarning(
                    "Skipped durable execution {ExecutionId} during project-scoped deletion because its manifest scope could not be read.",
                    candidate.Id
                );
                continue;
            }

            if (
                scope.ProjectId == projectId
                && (!conversationId.HasValue || scope.ProjectConversationId == conversationId.Value)
            )
            {
                executionIds.Add(candidate.Id);
            }
        }

        if (executionIds.Count == 0)
        {
            return;
        }

        await _dbContext
            .DurableExecutionEvents.Where(entry => executionIds.Contains(entry.ExecutionId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await _dbContext
            .DurableExecutions.Where(execution => executionIds.Contains(execution.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryGetDurableExecutionScope(string manifestJson, out DurableExecutionProjectScope scope)
    {
        scope = default;
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            if (
                !TryGetProperty(document.RootElement, "task", out var task)
                || !TryGetGuidProperty(task, "projectId", out var projectId)
                || !TryGetGuidProperty(task, "projectConversationId", out var projectConversationId)
            )
            {
                return false;
            }

            scope = new DurableExecutionProjectScope(projectId, projectConversationId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetGuidProperty(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return TryGetProperty(element, name, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out value);
    }

    private readonly record struct DurableExecutionProjectScope(Guid ProjectId, Guid ProjectConversationId);

    private async Task<bool> ExecuteProjectAsync(
        Guid projectId,
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken
    )
    {
        await using var lifecycleLease = await _applicationLock
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(projectId), cancellationToken)
            .ConfigureAwait(false);
        return await ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
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
