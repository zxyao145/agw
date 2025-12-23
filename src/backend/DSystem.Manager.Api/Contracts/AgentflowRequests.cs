using DSystem.Domain.Enums;
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
    string SystemPrompt,
    AgentflowOrchestrationPattern Pattern,
    string? ConfigurationJson,
    bool Enable,
    IReadOnlyList<AgentflowNodeRequest> Nodes,
    IReadOnlyList<AgentflowEdgeRequest> Edges);

public record AgentflowUpdateRequest(
    string Name,
    string? Description,
    string SystemPrompt,
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
    Guid AgentflowId,
    AgentflowOrchestrationPattern Pattern,
    bool NotImplemented,
    string? Message,
    string Input,
    string? FinalOutput,
    IReadOnlyList<WaChatMessage> Outputs)
{
    public static AgentflowExecuteResponse FromDomain(AgentflowExecutionResult result) =>
        new(
            result.AgentflowId,
            result.Pattern,
            result.NotImplemented,
            result.Message,
            result.Input,
            result.FinalOutput,
            result.Outputs);
}