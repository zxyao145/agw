namespace Agw.Agents.Execution.Inbound.Facades;

public sealed record ResolvedAgentTarget(AgentTargetKind Kind, Guid Id);

public interface IAgentExecutionRunner
{
    Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        ResolvedAgentTarget target,
        CancellationToken cancellationToken
    );
    IAsyncEnumerable<AgentExecutionEvent> ExecuteStreamingAsync(
        AgentExecutionRequest request,
        ResolvedAgentTarget target,
        CancellationToken cancellationToken
    );
}
