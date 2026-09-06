using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Application.Persistence;

public interface IAgentSessionStatePersistence
{
    Task<Guid?> ResolveProjectConversationIdAsync(
        Guid projectId,
        string contextId,
        Guid? projectConversationId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    );

    Task<string?> ReadAsync(
        Guid projectId,
        Guid projectConversationId,
        Guid agentId,
        string agentflowNodeId,
        string ownerUserId,
        CancellationToken cancellationToken = default,
        int expectedGeneration = 0
    );

    Task<bool> SaveAsync(
        Guid projectId,
        Guid projectConversationId,
        Guid agentId,
        string agentflowNodeId,
        string serializedSession,
        string ownerUserId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default,
        int expectedGeneration = 0
    );

    Task<AgentType?> GetAgentTypeAsync(Guid agentId, string ownerUserId, CancellationToken cancellationToken = default);
}
