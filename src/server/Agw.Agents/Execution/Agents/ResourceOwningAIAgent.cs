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

        Exception? resourceFailure = null;
        try
        {
            await _ownedResources.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            resourceFailure = exception;
        }

        if (failure != null && resourceFailure != null)
        {
            ExceptionDispatchInfo.Capture(
                new AggregateException(failure, resourceFailure)).Throw();
        }

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (resourceFailure != null)
        {
            ExceptionDispatchInfo.Capture(resourceFailure).Throw();
        }
    }
}
