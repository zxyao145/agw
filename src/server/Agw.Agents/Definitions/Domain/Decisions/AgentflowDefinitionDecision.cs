using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Domain.Decisions;

public sealed class AgentflowDefinitionDecision
{
    public IReadOnlyList<AgentflowNode>? Nodes { get; init; }

    public IReadOnlyList<AgentflowEdge>? Edges { get; init; }
}
