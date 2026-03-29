using Agw.Shared.Enums;

namespace Agw.Api.Contracts;

public record AgentExecutionRequest(
    AgentRuntimeType AgentType,
    string Input,
    string? SessionId = null,
    Guid? ProjectId = null,
    Guid? TaskId = null);
