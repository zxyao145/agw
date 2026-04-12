using Agw.Agents.Application.Agentflows;
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Shared.Models;

namespace Agw.Agents.Contracts;

public record AgentExecutionResponse(string? TaskId, IReadOnlyList<AgwMessage> Messages)
{
    public static AgentExecutionResponse FromAgentResult(AgentExecutionResult result) =>
        new(result.TaskId, result.Messages);

    public static AgentExecutionResponse FromAgentflowResult(AgentflowExecutionResult result) =>
        new(result.TaskId, result.Messages);
}
