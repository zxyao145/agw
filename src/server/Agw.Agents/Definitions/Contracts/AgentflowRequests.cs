using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Contracts;

public record AgentflowNodeRequest(
    string NodeId,
    AgentflowNodeKind Kind,
    Guid? RelateId,
    string? Name,
    string? PositionJson,
    string? Instructions,
    string? ConfigJson
);

public record AgentflowEdgeRequest(
    string EdgeId,
    string SourceNodeId,
    string TargetNodeId,
    AgentflowEdgeKind Kind,
    string? Label,
    string? ConditionJson,
    string? ConfigJson
);

public record AgentflowCreateRequest(
    string Name,
    string? Description,
    IReadOnlyList<AgentflowNodeRequest> Nodes,
    IReadOnlyList<AgentflowEdgeRequest> Edges,
    Guid? SummaryModelProviderId = null
);

public sealed record AgentflowEnabledUpdateRequest(Guid AgentflowId, bool Enable);

public record AgentflowUpdateRequest(
    string Name,
    string? Description,
    IReadOnlyList<AgentflowNodeRequest> Nodes,
    IReadOnlyList<AgentflowEdgeRequest> Edges,
    Guid? SummaryModelProviderId = null
);
