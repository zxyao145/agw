using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Agents;

public sealed class AgentSessionStatePersistence : IAgentSessionStatePersistence
{
    private readonly AgwDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;

    public AgentSessionStatePersistence(AgwDbContext dbContext, IApplicationLock? applicationLock = null)
    {
        _dbContext = dbContext;
        _applicationLock = applicationLock ?? InMemoryApplicationLock.Shared;
    }

    public async Task<Guid?> ResolveProjectConversationIdAsync(
        Guid projectId,
        string contextId,
        Guid? projectConversationId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    )
    {
        var query = _dbContext.OwnedProjectConversations(projectId, ownerUserId);
        query =
            projectConversationId.HasValue && projectConversationId.Value != Guid.Empty
                ? query.Where(conversation => conversation.Id == projectConversationId.Value)
                : query.Where(conversation => conversation.ContextId == contextId);

        return await query
            .Select(conversation => (Guid?)conversation.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string?> ReadAsync(
        Guid projectId,
        Guid projectConversationId,
        Guid agentId,
        string agentflowNodeId,
        string ownerUserId,
        CancellationToken cancellationToken = default,
        int expectedGeneration = 0
    ) =>
        await _dbContext
            .AgentSessionStates.AsNoTracking()
            .Where(entry =>
                entry.ProjectConversationId == projectConversationId
                && entry.AgentId == agentId
                && entry.AgentflowNodeId == agentflowNodeId
                && entry.Agent!.CreateBy == ownerUserId
                && entry.ProjectConversation!.ProjectId == projectId
                && entry.ProjectConversation.Generation == expectedGeneration
                && entry.ProjectConversation.CreateBy == ownerUserId
                && entry.ProjectConversation.Project!.CreateBy == ownerUserId
            )
            .Select(entry => entry.SerializedSession)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> SaveAsync(
        Guid projectId,
        Guid projectConversationId,
        Guid agentId,
        string agentflowNodeId,
        string serializedSession,
        string ownerUserId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default,
        int expectedGeneration = 0
    )
    {
        await using var lease = await _applicationLock.AcquireAsync(
            AgentDefinitionLock.GetResourceName(ownerUserId),
            cancellationToken
        );
        using var mutation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.HandleLostToken);
        cancellationToken = mutation.Token;
        if (
            !await _dbContext.Agents.AnyAsync(
                agent => agent.Id == agentId && agent.CreateBy == ownerUserId,
                cancellationToken
            )
        )
        {
            return false;
        }

        await using var transaction = await _dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var currentGeneration = await _dbContext
            .OwnedProjectConversations(projectId, ownerUserId)
            .Where(conversation => conversation.Id == projectConversationId)
            .Select(conversation => (int?)conversation.Generation)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!currentGeneration.HasValue)
        {
            return false;
        }
        if (currentGeneration.Value != expectedGeneration)
        {
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }

        var entry = await _dbContext
            .AgentSessionStates.SingleOrDefaultAsync(
                item =>
                    item.ProjectConversationId == projectConversationId
                    && item.AgentId == agentId
                    && item.AgentflowNodeId == agentflowNodeId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (entry == null)
        {
            entry = new AgentSessionStateEntry
            {
                ProjectConversationId = projectConversationId,
                AgentId = agentId,
                AgentflowNodeId = agentflowNodeId,
            };
            _dbContext.AgentSessionStates.Add(entry);
        }

        entry.SerializedSession = serializedSession;
        entry.UpdatedAt = updatedAt;
        await _dbContext
            .SaveConversationChangesAsync(projectConversationId, expectedGeneration, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<AgentType?> GetAgentTypeAsync(
        Guid agentId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    ) =>
        await _dbContext
            .Agents.AsNoTracking()
            .Where(agent => agent.Id == agentId && agent.CreateBy == ownerUserId)
            .Select(agent => (AgentType?)agent.Type)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}
