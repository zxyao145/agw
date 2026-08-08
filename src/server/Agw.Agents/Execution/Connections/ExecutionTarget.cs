using Agw.Shared.Data;

namespace Agw.Agents.Execution.Connections;

public readonly record struct ExecutionTarget(Guid AgentId, AgentRuntimeType AgentType);
