using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Execution;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Agents;

public sealed class AgentDeletionCoordinator : IAgentDeletionCoordinator
{
    private readonly AgwDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;

    public AgentDeletionCoordinator(AgwDbContext dbContext, IApplicationLock applicationLock)
    {
        _dbContext = dbContext;
        _applicationLock = applicationLock;
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
        if (
            !await _dbContext.Agents.AnyAsync(
                agent => agent.Id == agentId && agent.CreateBy == ownerUserId,
                cancellationToken
            )
        )
        {
            return false;
        }

        if (
            await _dbContext.AgentflowNodes.AnyAsync(
                node => node.Kind == AgentflowNodeKind.Agent && node.RelateId == agentId,
                cancellationToken
            )
            || await _dbContext.Jobs.AnyAsync(
                job => job.AgentType == AgentRuntimeType.Agent && job.AgentId == agentId,
                cancellationToken
            )
        )
        {
            throw new AgwException(ErrorCodes.AgentInUse);
        }

        // The Agent owner remains authoritative when related catalogs or conversations are already missing.
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
            .Agents.Where(agent => agent.Id == agentId && agent.CreateBy == ownerUserId)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
