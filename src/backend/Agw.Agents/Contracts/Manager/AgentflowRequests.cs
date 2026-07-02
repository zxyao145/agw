using Agw.Agents.Application.Agentflows;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;

namespace Agw.Agents.Contracts.Manager;

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
    IReadOnlyList<AgentflowEdgeRequest> Edges);

public record AgentflowUpdateRequest(
    string Name,
    string? Description,
    bool Enable,
    IReadOnlyList<AgentflowNodeRequest> Nodes,
    IReadOnlyList<AgentflowEdgeRequest> Edges);

public record AgentflowExecuteRequest(string Input);

public record AgentflowExecutionAgentResultResponse(Guid AgentId, string AgentName, int Order, string Output)
{
    public static AgentflowExecutionAgentResultResponse FromDomain(AgentflowExecutionAgentResult result) =>
        new(result.AgentId, result.AgentName, result.Order, result.Output);
}

public record AgentflowExecuteResponse(
    string ContextId,
    IReadOnlyList<AgwMessage> Messages)
{
    public static AgentflowExecuteResponse FromDomain(AgentflowExecutionResult result) =>
        new(
            result.ContextId,
            result.Messages);
}
