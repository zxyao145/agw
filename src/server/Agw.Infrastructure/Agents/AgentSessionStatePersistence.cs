using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Agents;

public sealed class AgentSessionStatePersistence : IAgentSessionStatePersistence
{
    private readonly AgwDbContext _dbContext;

    public AgentSessionStatePersistence(AgwDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Guid?> ResolveProjectConversationIdAsync(
        Guid projectId,
        string contextId,
        Guid? projectConversationId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    )
    {
        var query = OwnedProjectConversations(projectId, ownerUserId);
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
        CancellationToken cancellationToken = default
    )
    {
        if (
            !await OwnedProjectConversations(projectId, ownerUserId)
                .AnyAsync(conversation => conversation.Id == projectConversationId, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            return null;
        }

        return await _dbContext
            .AgentSessionStates.AsNoTracking()
            .Where(entry =>
                entry.ProjectConversationId == projectConversationId
                && entry.AgentId == agentId
                && entry.AgentflowNodeId == agentflowNodeId
            )
            .Select(entry => entry.SerializedSession)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> SaveAsync(
        Guid projectId,
        Guid projectConversationId,
        Guid agentId,
        string agentflowNodeId,
        string serializedSession,
        string ownerUserId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default
    )
    {
        await using var transaction = await _dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (
            !await OwnedProjectConversations(projectId, ownerUserId)
                .AnyAsync(conversation => conversation.Id == projectConversationId, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            return false;
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
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    private IQueryable<Agw.Shared.Data.Entities.Projects.ProjectConversation> OwnedProjectConversations(
        Guid projectId,
        string ownerUserId
    ) =>
        _dbContext
            .ProjectConversations.AsNoTracking()
            .Where(conversation =>
                conversation.ProjectId == projectId
                && conversation.CreateBy == ownerUserId
                && _dbContext.Projects.Any(project =>
                    project.Id == conversation.ProjectId && project.CreateBy == ownerUserId
                )
            );
}
