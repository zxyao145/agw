using DSystem.Appliaction.Services;
using DSystem.Shared.Enums;
using DSystem.Shared.Models;

namespace DSystem.Api.Contracts;

public record AgentExecutionRequest(
    ProjectTaskAgentType AgentType,
    string Input,
    string? ThreadId = null,
    Guid? ProjectId = null);

public record AgentExecutionResponse(
    string? ThreadId,
    IReadOnlyList<AiMessage> Messages)
{
    public static AgentExecutionResponse FromAgentResult(AgentExecutionResult result) =>
        new(result.ThreadId, result.Messages);

    public static AgentExecutionResponse FromAgentflowResult(AgentflowExecutionResult result) =>
        new(result.ThreadId, result.Messages);
}
