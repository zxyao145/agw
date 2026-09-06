using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Application.Persistence;

public interface IAgentflowDefinitionReader
{
    Task<Agentflow?> FindVisibleAsync(
        Guid agentflowId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<AgentflowNode>> ListNodesAsync(Guid agentflowId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentflowEdge>> ListEdgesAsync(Guid agentflowId, CancellationToken cancellationToken = default);
}
