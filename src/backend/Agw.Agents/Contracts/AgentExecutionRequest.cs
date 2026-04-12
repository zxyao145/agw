using Agw.Shared.Contracts.Agents;

namespace Agw.Agents.Contracts;

public record AgentExecutionRequest(
    AgentRuntimeType AgentType,
    string Input,
    Guid? ProjectId = null,
    Guid? TaskId = null);
