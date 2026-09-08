namespace Agw.Agents.Execution.Inbound.Connections;

public readonly record struct ExecutionTarget(Guid AgentId, AgentRuntimeType AgentType);
