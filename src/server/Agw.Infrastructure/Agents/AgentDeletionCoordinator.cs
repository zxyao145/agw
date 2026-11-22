using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Agents;

public sealed class AgentDeletionCoordinator : IAgentDeletionCoordinator
{
    private readonly AgwDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;
    private readonly IAgentflowRevisionUsageReader _revisionUsageReader;

    public AgentDeletionCoordinator(
        AgwDbContext dbContext,
        IApplicationLock applicationLock,
        IAgentflowRevisionUsageReader revisionUsageReader
    )
    {
        _dbContext = dbContext;
        _applicationLock = applicationLock;
        _revisionUsageReader = revisionUsageReader;
    }

    public async Task<bool> DeleteAsync(Guid agentId, string ownerUserId, CancellationToken cancellationToken = default)
    {
        await using var lease = await _applicationLock.AcquireAsync(
            AgentDefinitionLock.GetResourceName(ownerUserId),
            cancellationToken
        );
        using var mutation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.HandleLostToken);
        cancellationToken = mutation.Token;
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await AgentExistsAsync(agentId, ownerUserId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (await IsReferencedAsync(agentId, ownerUserId, cancellationToken).ConfigureAwait(false))
        {
            throw new AgwException(ErrorCodes.AgentInUse);
        }

        await DeleteOwnedAgentDataAsync(agentId, ownerUserId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private Task<bool> AgentExistsAsync(Guid agentId, string ownerUserId, CancellationToken cancellationToken) =>
        _dbContext.Agents.AnyAsync(agent => agent.Id == agentId && agent.CreateBy == ownerUserId, cancellationToken);

    private async Task<bool> IsReferencedAsync(Guid agentId, string ownerUserId, CancellationToken cancellationToken)
    {
        if (await HasLatestOrPublishedReferenceAsync(agentId, ownerUserId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var pinnedRevisions = await _revisionUsageReader
            .ListPinnedRevisionsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (
            await HasPinnedReferenceAsync(agentId, ownerUserId, pinnedRevisions, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            return true;
        }

        return await _dbContext.EventSubscriptions.AnyAsync(
            row =>
                row.ManagedBy == EventManagedBy.Job
                && row.TargetKind == EventSubscriptionTargetKind.Agent
                && row.AgentId == agentId
                && row.CreateBy == ownerUserId,
            cancellationToken
        );
    }

    private async Task<bool> HasLatestOrPublishedReferenceAsync(
        Guid agentId,
        string ownerUserId,
        CancellationToken cancellationToken
    )
    {
        var referencedByNode = await _dbContext.AgentflowNodes.AnyAsync(
            node =>
                node.Kind == AgentflowNodeKind.Agent
                && node.RelateId == agentId
                && _dbContext.Agentflows.Any(agentflow =>
                    agentflow.Id == node.AgentflowId
                    && agentflow.CreateBy == ownerUserId
                    && (
                        node.AgentflowRevision == agentflow.LatestRevision
                        || node.AgentflowRevision == agentflow.PublishedRevision
                    )
                ),
            cancellationToken
        );
        if (referencedByNode)
        {
            return true;
        }

        return await _dbContext.AgentflowBlockMembers.AnyAsync(
            member =>
                member.Kind == AgentflowNodeKind.Agent
                && member.RelateId == agentId
                && _dbContext.Agentflows.Any(agentflow =>
                    agentflow.Id == member.AgentflowId
                    && agentflow.CreateBy == ownerUserId
                    && (
                        member.AgentflowRevision == agentflow.LatestRevision
                        || member.AgentflowRevision == agentflow.PublishedRevision
                    )
                ),
            cancellationToken
        );
    }

    private async Task<bool> HasPinnedReferenceAsync(
        Guid agentId,
        string ownerUserId,
        IReadOnlySet<(Guid AgentflowId, int Revision)> pinnedRevisions,
        CancellationToken cancellationToken
    )
    {
        var nodeRevisions = await _dbContext
            .AgentflowNodes.Where(node =>
                node.Kind == AgentflowNodeKind.Agent
                && node.RelateId == agentId
                && _dbContext.Agentflows.Any(agentflow =>
                    agentflow.Id == node.AgentflowId && agentflow.CreateBy == ownerUserId
                )
            )
            .Select(node => new { node.AgentflowId, node.AgentflowRevision })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (nodeRevisions.Any(node => pinnedRevisions.Contains((node.AgentflowId, node.AgentflowRevision))))
        {
            return true;
        }

        var memberRevisions = await _dbContext
            .AgentflowBlockMembers.Where(member =>
                member.Kind == AgentflowNodeKind.Agent
                && member.RelateId == agentId
                && _dbContext.Agentflows.Any(agentflow =>
                    agentflow.Id == member.AgentflowId && agentflow.CreateBy == ownerUserId
                )
            )
            .Select(member => new { member.AgentflowId, member.AgentflowRevision })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return memberRevisions.Any(member => pinnedRevisions.Contains((member.AgentflowId, member.AgentflowRevision)));
    }

    private async Task DeleteOwnedAgentDataAsync(Guid agentId, string ownerUserId, CancellationToken cancellationToken)
    {
        // The Agent owner remains authoritative when related catalogs or conversations are already missing.
        // Agent 的所属用户始终是清理依据，即使关联目录或对话已经缺失。
        var ownedAgents = _dbContext
            .Agents.Where(agent => agent.Id == agentId && agent.CreateBy == ownerUserId)
            .Select(agent => agent.Id);
        await _dbContext
            .AgentSessionStates.IgnoreUserScope()
            .Where(state => state.AgentId == agentId && ownedAgents.Contains(state.AgentId))
            .ExecuteDeleteAsync(cancellationToken);
        await _dbContext
            .ProjectConversationBindings.IgnoreUserScope()
            .Where(binding => binding.AgentId == agentId && binding.CreateBy == ownerUserId)
            .ExecuteDeleteAsync(cancellationToken);
        await _dbContext
            .AgentSkillRelations.IgnoreUserScope()
            .Where(relation => relation.AgentId == agentId && ownedAgents.Contains(relation.AgentId))
            .ExecuteDeleteAsync(cancellationToken);
        await _dbContext
            .AgentConnectionRelations.IgnoreUserScope()
            .Where(relation => relation.AgentId == agentId && ownedAgents.Contains(relation.AgentId))
            .ExecuteDeleteAsync(cancellationToken);
        await _dbContext
            .AgentMcpToolServers.IgnoreUserScope()
            .Where(relation => relation.AgentId == agentId && ownedAgents.Contains(relation.AgentId))
            .ExecuteDeleteAsync(cancellationToken);
        await _dbContext
            .AgentA2AEndpoints.Where(endpoint => endpoint.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);
        await _dbContext
            .Agents.Where(agent => agent.Id == agentId && agent.CreateBy == ownerUserId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
