using Agw.Agents.Execution.Agents;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows;

public sealed class AgentflowWorkflowLease : IAsyncDisposable
{
    private readonly AgentResourceLease _resources;

    internal AgentflowWorkflowLease(Workflow workflow, AgentResourceLease resources)
    {
        Workflow = workflow;
        _resources = resources;
    }

    public Workflow Workflow { get; }

    public ValueTask DisposeAsync()
    {
        return _resources.DisposeAsync();
    }
}

internal sealed class AgentflowAgentLifetime : IAsyncDisposable
{
    private readonly AIAgent _agent;
    private int _disposed;

    public AgentflowAgentLifetime(AIAgent agent)
    {
        _agent = agent;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        switch (_agent)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
