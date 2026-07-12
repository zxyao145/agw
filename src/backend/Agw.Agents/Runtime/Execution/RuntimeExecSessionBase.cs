using Agw.Agents.Runtime.Contracts;

namespace Agw.Agents.Runtime.Execution;

public abstract class RuntimeExecSessionBase : IAsyncDisposable
{
    private readonly object _lock = new();
    private ActiveTurn? _activeTurn;
    private Task _whenIdle = Task.CompletedTask;
    private bool _disposed;

    public ActiveTurn? ActiveTurn
    {
        get
        {
            lock (_lock)
            {
                return _activeTurn;
            }
        }
    }

    public bool HasActiveTurn => ActiveTurn is { IsCompleted: false };

    public bool TryStartTurn(ActiveTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        TaskCompletionSource idleCompletion;

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeTurn is { IsCompleted: false })
            {
                return false;
            }

            _activeTurn = turn;
            idleCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _whenIdle = idleCompletion.Task;
        }

        _ = ObserveTurnAsync(turn, idleCompletion);
        return true;
    }

    public Task WhenIdleAsync()
    {
        lock (_lock)
        {
            return _whenIdle;
        }
    }

    public void RequestInterrupt(string? reason)
    {
        ActiveTurn?.RequestInterrupt(reason);
    }

    public ValueTask<bool> TrySubmitHumanResponseAsync(
        HumanResponseCommand command,
        CancellationToken cancellationToken)
    {
        return ActiveTurn?.TrySubmitHumanResponseAsync(command, cancellationToken)
            ?? ValueTask.FromResult(false);
    }

    public virtual async ValueTask DisposeAsync()
    {
        Task whenIdle;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeTurn?.RequestInterrupt(null);
            whenIdle = _whenIdle;
        }

        await whenIdle;
    }

    private async Task ObserveTurnAsync(ActiveTurn turn, TaskCompletionSource idleCompletion)
    {
        try
        {
            await turn.ExecutionTask;
        }
        catch (Exception)
        {
        }
        finally
        {
            await turn.DisposeAsync();
            lock (_lock)
            {
                if (ReferenceEquals(_activeTurn, turn))
                {
                    _activeTurn = null;
                }
            }

            idleCompletion.TrySetResult();
        }
    }
}
