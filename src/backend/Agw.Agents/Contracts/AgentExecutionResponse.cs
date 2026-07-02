using Agw.Agents.Application.Agentflows;
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Contracts;

public record AgentExecutionResponse(string ContextId, IReadOnlyList<AgwMessage> Messages)
{
    public static AgentExecutionResponse FromAgentResult(AgentExecutionResult result) =>
        new(result.ContextId, result.Messages);

    public static AgentExecutionResponse FromAgentflowResult(AgentflowExecutionResult result) =>
        new(result.ContextId, result.Messages);
}
