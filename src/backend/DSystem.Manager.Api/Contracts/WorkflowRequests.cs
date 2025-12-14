using DSystem.Domain.Enums;
using DSystem.Domain.Services;

namespace DSystem.Manager.Api.Contracts;

public record WorkflowAgentBindingRequest(Guid AgentId, int Order, string? Role);

public record WorkflowCreateRequest(
    string Name,
    string? Description,
    WorkflowOrchestrationPattern Pattern,
    string? ConfigurationJson,
    bool Enable,
    IReadOnlyList<WorkflowAgentBindingRequest> Agents);

public record WorkflowUpdateRequest(
    string Name,
    string? Description,
    WorkflowOrchestrationPattern Pattern,
    string? ConfigurationJson,
    bool Enable,
    IReadOnlyList<WorkflowAgentBindingRequest> Agents);

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
    IReadOnlyList<WorkflowExecutionAgentResultResponse> Outputs)
{
    public static WorkflowExecuteResponse FromDomain(WorkflowExecutionResult result) =>
        new(
            result.WorkflowId,
            result.Pattern,
            result.NotImplemented,
            result.Message,
            result.Input,
            result.FinalOutput,
            result.Outputs.Select(WorkflowExecutionAgentResultResponse.FromDomain).ToList());
}