using Agw.Shared.Contracts.Agents;

namespace Agw.Api.Contracts;

public record AgentExecutionRequest(
    AgentRuntimeType AgentType,
    string Input,
    Guid? ProjectId = null,
    Guid? TaskId = null);
