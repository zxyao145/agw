using Agw.Shared.Contracts.Agents;

namespace Agw.Agents.Definitions.Contracts;

public record AgentflowNodeRequest(
    string NodeId,
    AgentflowNodeKind Kind,
    Guid? RelateId,
    string? Name,
    string? PositionJson,
    string? Instructions,
    string? ConfigJson);

public record AgentflowEdgeRequest(
    string EdgeId,
    string SourceNodeId,
    string TargetNodeId,
    AgentflowEdgeKind Kind,
    string? Label,
    string? ConditionJson,
    string? ConfigJson);

public record AgentflowCreateRequest(
    string Name,
    string? Description,
    bool Enable,
    IReadOnlyList<AgentflowNodeRequest> Nodes,
    IReadOnlyList<AgentflowEdgeRequest> Edges,
    Guid? SummaryModelProviderId = null);

public record AgentflowUpdateRequest(
    string Name,
    string? Description,
    bool Enable,
    IReadOnlyList<AgentflowNodeRequest> Nodes,
    IReadOnlyList<AgentflowEdgeRequest> Edges,
    Guid? SummaryModelProviderId = null);
