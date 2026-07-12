namespace Agw.Agents.Execution.Agentflows.Observability;

internal sealed record AgentflowExecutionTraceContext(
    Guid ProjectId,
    string ContextId,
    Guid TaskId);
