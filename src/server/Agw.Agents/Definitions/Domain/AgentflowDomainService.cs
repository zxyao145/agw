using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Policies;
using Agw.Agents.Definitions.Domain.Topology;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Domain;

/// <summary>
/// Compatibility adapter for the pre-Behavior Agentflow definition service.
/// New application code must use <see cref="AgentflowBehavior"/> and
/// <see cref="AgentflowDefinitionPolicy"/> directly.
/// </summary>
[Obsolete("Use AgentflowDefinitionPolicy for decisions and AgentflowBehavior for graph mutation.")]
public sealed class AgentflowDomainService
{
    private readonly TimeProvider _timeProvider;
    private readonly AgentflowDefinitionPolicy _definitionPolicy = new();

    public AgentflowDomainService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool TryPrepareForCreate(Agentflow agentflow, string user)
    {
        ArgumentNullException.ThrowIfNull(agentflow);
        if (string.IsNullOrWhiteSpace(agentflow.Name))
        {
            return false;
        }

        agentflow.Id = agentflow.Id == Guid.Empty ? Guid.CreateVersion7() : agentflow.Id;
        agentflow.CreateBy = user;
        agentflow.CreateTime = _timeProvider.GetUtcNow();
        return true;
    }

    public bool TryApplyUpdate(Agentflow existing, Action<Agentflow> updateAction, string user)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(updateAction);

        updateAction(existing);
        if (string.IsNullOrWhiteSpace(existing.Name))
        {
            return false;
        }

        existing.UpdateBy = user;
        existing.UpdateTime = _timeProvider.GetUtcNow();
        return true;
    }

    public (IReadOnlyList<AgentflowNode>? Nodes, IReadOnlyList<AgentflowEdge>? Edges) ValidateAndNormalizeGraph(
        IReadOnlyList<AgentflowNode>? nodes,
        IReadOnlyList<AgentflowEdge>? edges,
        Guid agentflowId,
        IReadOnlyCollection<Guid> existingAgentIds,
        Guid? summaryModelProviderId = null,
        IReadOnlyCollection<Guid>? existingModelProviderIds = null,
        IReadOnlyDictionary<Guid, string>? existingAgentNames = null
    )
    {
        var decision = _definitionPolicy.Evaluate(
            nodes,
            edges,
            agentflowId,
            existingAgentIds,
            summaryModelProviderId,
            existingModelProviderIds,
            existingAgentNames
        );
        return (decision.Nodes, decision.Edges);
    }

    public IReadOnlyList<AgentflowNode> OrderNodesByEdges(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges
    ) => AgentflowTopology.OrderNodesByEdges(nodes, edges);
}
