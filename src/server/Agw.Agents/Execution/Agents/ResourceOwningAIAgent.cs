using System.Runtime.ExceptionServices;

using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents;

internal sealed class ResourceOwningAIAgent : DelegatingAIAgent, IAsyncDisposable
{
    private readonly IAsyncDisposable _ownedResources;
    private int _disposed;

    public ResourceOwningAIAgent(AIAgent innerAgent, IAsyncDisposable ownedResources)
        : base(innerAgent)
    {
        _ownedResources = ownedResources;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            switch (InnerAgent)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await _ownedResources.DisposeAsync().ConfigureAwait(false);
        }
        catch when (failure != null)
        {
        }

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
