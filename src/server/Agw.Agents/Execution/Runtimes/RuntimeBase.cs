using Agw.Agents.Execution.Contracts;
using Agw.Agents.Execution.Turns;

namespace Agw.Agents.Execution.Runtimes;

public abstract class RuntimeBase : IAsyncDisposable
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

    public ActiveTurn? StartTurn(
        RuntimeTurnContext turnContext,
        IRuntimeTurnContextAccessor turnContextAccessor,
        CancellationTokenSource executionCts,
        Action interruptAction,
        Func<CancellationToken, Task> executeAsync,
        Func<HumanResponseCommand, CancellationToken, ValueTask<bool>>? submitHumanResponseAsync = null)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionTask = RunAfterRegistrationAsync(
            start.Task,
            turnContext,
            turnContextAccessor,
            executeAsync,
            executionCts.Token);
        var activeTurn = new ActiveTurn(
            executionTask,
            executionCts,
            interruptAction,
            submitHumanResponseAsync);
        if (!TryStartTurn(activeTurn))
        {
            executionCts.Cancel();
            start.TrySetCanceled();
            _ = activeTurn.DisposeAsync();
            return null;
        }

        start.SetResult();
        return activeTurn;
    }

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

    public void RequestInterrupt()
    {
        ActiveTurn?.RequestInterrupt();
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
            _activeTurn?.RequestInterrupt();
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

    private static async Task RunAfterRegistrationAsync(
        Task registration,
        RuntimeTurnContext turnContext,
        IRuntimeTurnContextAccessor turnContextAccessor,
        Func<CancellationToken, Task> executeAsync,
        CancellationToken cancellationToken)
    {
        await registration;
        using var scope = turnContextAccessor.Push(turnContext);
        await executeAsync(cancellationToken);
    }
}
