using DSystem.Domain.Enums;
using DSystem.Domain.Services;

namespace DSystem.Manager.Api.Contracts;

public record WorkflowNodeRequest(
    string NodeId,
    WorkflowNodeType Type,
    Guid RelateId);

public record WorkflowEdgeRequest(
    string EdgeId,
    string SourceNodeId,
    string TargetNodeId,
    bool Animated);

public record WorkflowCreateRequest(
    string Name,
    string? Description,
    string SystemPrompt,
    WorkflowOrchestrationPattern Pattern,
    string? ConfigurationJson,
    bool Enable,
    IReadOnlyList<WorkflowNodeRequest> Nodes,
    IReadOnlyList<WorkflowEdgeRequest> Edges);

public record WorkflowUpdateRequest(
    string Name,
    string? Description,
    string SystemPrompt,
    WorkflowOrchestrationPattern Pattern,
    string? ConfigurationJson,
    bool Enable,
    IReadOnlyList<WorkflowNodeRequest> Nodes,
    IReadOnlyList<WorkflowEdgeRequest> Edges);

public record WorkflowExecuteRequest(string Input);

public record WorkflowExecutionAgentResultResponse(Guid AgentId, string AgentName, int Order, string Output)
{
    public static WorkflowExecutionAgentResultResponse FromDomain(WorkflowExecutionAgentResult result) =>
        new(result.AgentId, result.AgentName, result.Order, result.Output);
}

public record WorkflowExecuteResponse(
    Guid WorkflowId,
    WorkflowOrchestrationPattern Pattern,
    bool NotImplemented,
    string? Message,
    string Input,
    string? FinalOutput,
    IReadOnlyList<WaChatMessage> Outputs)
{
    public static WorkflowExecuteResponse FromDomain(WorkflowExecutionResult result) =>
        new(
            result.WorkflowId,
            result.Pattern,
            result.NotImplemented,
            result.Message,
            result.Input,
            result.FinalOutput,
            result.Outputs);
}