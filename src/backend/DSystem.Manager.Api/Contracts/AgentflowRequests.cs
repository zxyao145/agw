using DSystem.Domain.Enums;
using DSystem.Domain.Models;
using DSystem.Domain.Services;

namespace DSystem.Manager.Api.Contracts;

public record AgentflowNodeRequest(
    string NodeId,
    AgentflowNodeType Type,
    Guid RelateId);

public record AgentflowEdgeRequest(
    string EdgeId,
    string SourceNodeId,
    string TargetNodeId,
    bool Animated);

public record AgentflowCreateRequest(
    string Name,
    string? Description,
    AgentflowOrchestrationPattern Pattern,
    string? ConfigurationJson,
    bool Enable,
    IReadOnlyList<AgentflowNodeRequest> Nodes,
    IReadOnlyList<AgentflowEdgeRequest> Edges);

public record AgentflowUpdateRequest(
    string Name,
    string? Description,
    AgentflowOrchestrationPattern Pattern,
    string? ConfigurationJson,
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
    string? ThreadId,
    IReadOnlyList<AiMessage2> Messages)
{
    public static AgentflowExecuteResponse FromDomain(AgentflowExecutionResult result) =>
        new(
            result.ThreadId,
            result.Messages);
}