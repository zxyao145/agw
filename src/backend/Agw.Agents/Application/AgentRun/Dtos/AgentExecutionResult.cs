using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Application.AgentRun.Dtos;

public record AgentExecutionResult(
    string TaskId,
    IReadOnlyList<AgwMessage> Messages);
