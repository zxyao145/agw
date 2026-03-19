using Agw.Appliaction.Services;
using Agw.Shared.Enums;
using Agw.Shared.Models;

namespace Agw.Api.Contracts;

public record AgentExecutionRequest(
    ProjectTaskAgentType AgentType,
    string Input,
    string? SessionId = null,
    string? ProjectId = null,
    Guid? TaskId = null);

public record AgentExecutionWsRequest(
    ProjectTaskAgentType AgentType,
    AgwUserInput Input,
    string? SessionId = null,
    string? ProjectId = null,
    Guid? TaskId = null);

public record AgentExecutionResponse(
    string? SessionId,
    IReadOnlyList<AgwMessage> Messages)
{
    public static AgentExecutionResponse FromAgentResult(AgentExecutionResult result) =>
        new(result.SessionId, result.Messages);

    public static AgentExecutionResponse FromAgentflowResult(AgentflowExecutionResult result) =>
        new(result.SessionId, result.Messages);
}
