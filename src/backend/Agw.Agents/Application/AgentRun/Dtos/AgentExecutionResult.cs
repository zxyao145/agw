using Agw.Shared.Models;

namespace Agw.Agents.Application.AgentRun.Dtos;

public record AgentExecutionResult(
    string TaskId,
    IReadOnlyList<AgwMessage> Messages);
