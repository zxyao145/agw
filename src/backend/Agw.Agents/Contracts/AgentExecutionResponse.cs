using Agw.Appliaction.Services.Agentflows;
using Agw.Appliaction.Services.Agents;
using Agw.Shared.Models;

namespace Agw.Api.Contracts;

public record AgentExecutionResponse(string? TaskId, IReadOnlyList<AgwMessage> Messages)
{
    public static AgentExecutionResponse FromAgentResult(AgentExecutionResult result) =>
        new(result.TaskId, result.Messages);

    public static AgentExecutionResponse FromAgentflowResult(AgentflowExecutionResult result) =>
        new(result.TaskId, result.Messages);
}
