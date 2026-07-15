using System.Collections;

using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Integrations.Mcp;

public sealed class ConnectionToolLease : IReadOnlyList<AITool>, IAsyncDisposable
{
    private readonly IReadOnlyList<AITool> _tools;
    private readonly IReadOnlyList<IAsyncDisposable> _resources;
    private int _disposed;

    internal ConnectionToolLease(
        IReadOnlyList<AITool> tools,
        IReadOnlyList<IAsyncDisposable> resources)
    {
        _tools = tools.ToArray();
        _resources = resources.ToArray();
    }

    public int Count => _tools.Count;

    public IReadOnlyList<AITool> Tools => _tools;

    public AITool this[int index] => _tools[index];

    public IEnumerator<AITool> GetEnumerator()
    {
        return _tools.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var failed = false;
        foreach (var resource in _resources)
        {
            try
            {
                await resource.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                failed = true;
            }
        }

        if (failed)
        {
            throw new AgwException(
                ErrorCodes.CannotCreateInstance,
                "Failed to release MCP connection resources.");
        }
    }
}
