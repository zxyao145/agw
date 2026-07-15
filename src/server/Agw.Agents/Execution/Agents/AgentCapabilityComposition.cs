using Agw.Integrations.Application.Capabilities;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents;

internal sealed class AgentCapabilityComposition : IAsyncDisposable
{
    private readonly AgentResourceLease _lease;

    public AgentCapabilityComposition(
        IReadOnlyList<AITool> tools,
        IReadOnlyList<PluginSkillReference> pluginSkills,
        IReadOnlyList<ConnectionCapabilityWarning> warnings,
        AgentResourceLease lease)
    {
        Tools = tools;
        PluginSkills = pluginSkills;
        Warnings = warnings;
        _lease = lease;
    }

    public IReadOnlyList<AITool> Tools { get; }

    public IReadOnlyList<PluginSkillReference> PluginSkills { get; }

    public IReadOnlyList<ConnectionCapabilityWarning> Warnings { get; }

    public ValueTask DisposeAsync()
    {
        return _lease.DisposeAsync();
    }
}

internal sealed class AgentResourceLease : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _resources = [];
    private int _disposed;

    public void Add(IAsyncDisposable resource)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _resources.Add(resource);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        for (var index = _resources.Count - 1; index >= 0; index--)
        {
            try
            {
                await _resources[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure != null)
        {
            throw failure;
        }
    }
}
