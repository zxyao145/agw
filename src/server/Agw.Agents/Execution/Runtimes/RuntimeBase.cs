using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Turns;

namespace Agw.Agents.Execution.Runtimes;

public abstract class RuntimeBase : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Func<CancellationToken, Task>> _afterTurnActions = new(StringComparer.Ordinal);
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
        RuntimeTurnContextAccessor turnContextAccessor,
        CancellationTokenSource executionCts,
        Action interruptAction,
        Func<CancellationToken, Task> executeAsync,
        Func<HumanResponseCommand, CancellationToken, ValueTask<bool>>? submitHumanResponseAsync = null,
        Func<PermissionMode, CancellationToken, ValueTask>? setPermissionModeAsync = null)
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
            submitHumanResponseAsync,
            setPermissionModeAsync);
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
            if (_activeTurn != null)
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

    public bool TryScheduleAfterTurn(
        string key,
        Func<CancellationToken, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(action);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeTurn == null)
            {
                return false;
            }

            _afterTurnActions[key] = action;
            return true;
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

    public ValueTask<bool> TrySetActivePermissionModeAsync(
        PermissionMode permissionMode,
        CancellationToken cancellationToken)
    {
        return ActiveTurn?.TrySetPermissionModeAsync(permissionMode, cancellationToken)
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
            var completed = false;
            while (!completed)
            {
                Func<CancellationToken, Task>[] actions;
                lock (_lock)
                {
                    if (!ReferenceEquals(_activeTurn, turn))
                    {
                        actions = [];
                        completed = true;
                    }
                    else if (_afterTurnActions.Count == 0)
                    {
                        _activeTurn = null;
                        actions = [];
                        completed = true;
                    }
                    else
                    {
                        actions = _afterTurnActions.Values.ToArray();
                        _afterTurnActions.Clear();
                    }
                }

                foreach (var action in actions)
                {
                    try
                    {
                        await action(CancellationToken.None);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            idleCompletion.TrySetResult();
        }
    }

    private static async Task RunAfterRegistrationAsync(
        Task registration,
        RuntimeTurnContext turnContext,
        RuntimeTurnContextAccessor turnContextAccessor,
        Func<CancellationToken, Task> executeAsync,
        CancellationToken cancellationToken)
    {
        await registration;
        using var scope = turnContextAccessor.Push(turnContext);
        await executeAsync(cancellationToken);
    }
}
