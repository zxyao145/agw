using DSystem.Appliaction.Services;
using DSystem.Shared.Enums;
using DSystem.Shared.Models;

namespace DSystem.Api.Contracts;

public record AgentExecutionRequest(
    ProjectTaskAgentType AgentType,
    string Input,
    string? SessionId = null,
    string? ProjectId = null,
    Guid? TaskId = null);

public record AgentExecutionResponse(
    string? SessionId,
    IReadOnlyList<AiMessage> Messages)
{
    public static AgentExecutionResponse FromAgentResult(AgentExecutionResult result) =>
        new(result.SessionId, result.Messages);

    public static AgentExecutionResponse FromAgentflowResult(AgentflowExecutionResult result) =>
        new(result.SessionId, result.Messages);
}
