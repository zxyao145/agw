using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Inbound.Connections;

namespace Agw.Agents.Execution.Runtimes.InProcess;

/// <summary>
/// 进程内“一个 Runtime 同时只跑一个 Turn”的生命周期：登记活动 Turn，排队在 Turn 结束后执行的动作，释放时中断并等待。
/// The in-process lifecycle of "one Runtime runs one turn at a time": registers the active turn, queues actions for after the turn, and interrupts and waits on release.
/// </summary>
internal sealed class InProcessTurnHost : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Func<CancellationToken, Task>> _afterTurnActions = new(StringComparer.Ordinal);
    private readonly IAsyncDisposable _runtime;
    private ActiveTurn? _activeTurn;
    private Task _whenIdle = Task.CompletedTask;
    private bool _disposed;

    public InProcessTurnHost(
        ExecutionTarget target,
        IAsyncDisposable runtime,
        string workspaceFingerprint,
        long permissionVersion
    )
    {
        Target = target;
        _runtime = runtime;
        WorkspaceFingerprint = workspaceFingerprint;
        PermissionVersion = permissionVersion;
    }

    public ExecutionTarget Target { get; }

    public AgentRuntime? AgentRuntime => _runtime as AgentRuntime;

    public AgentflowRuntime? AgentflowRuntime => _runtime as AgentflowRuntime;

    public string WorkspaceFingerprint { get; }

    /// <summary>
    /// 创建 Runtime 时的权限版本；外部 Engine 进程在启动时接收权限模式。
    /// The permission version when the Runtime was created; external Engine processes receive the permission mode at start.
    /// </summary>
    public long PermissionVersion { get; }

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
        ExecutionScope scope,
        CancellationTokenSource executionCts,
        Action interruptAction,
        Func<CancellationToken, Task> executeAsync,
        Func<InteractionResponse, CancellationToken, ValueTask<bool>>? submitHumanResponseAsync = null
    )
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionTask = RunAfterRegistrationAsync(start.Task, scope, executeAsync, executionCts.Token);
        var activeTurn = new ActiveTurn(executionTask, executionCts, interruptAction, submitHumanResponseAsync);
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

    public Task WhenIdleAsync()
    {
        lock (_lock)
        {
            return _whenIdle;
        }
    }

    public bool TryScheduleAfterTurn(string key, Func<CancellationToken, Task> action)
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

    public ValueTask<bool> TrySubmitHumanResponseAsync(InteractionResponse command, CancellationToken cancellationToken)
    {
        return ActiveTurn?.TrySubmitHumanResponseAsync(command, cancellationToken) ?? ValueTask.FromResult(false);
    }

    public async ValueTask DisposeAsync()
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
        await _runtime.DisposeAsync();
    }

    private bool TryStartTurn(ActiveTurn turn)
    {
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

    private async Task ObserveTurnAsync(ActiveTurn turn, TaskCompletionSource idleCompletion)
    {
        try
        {
            await turn.ExecutionTask;
        }
        catch (Exception) { }
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
                    catch (Exception) { }
                }
            }

            idleCompletion.TrySetResult();
        }
    }

    private static async Task RunAfterRegistrationAsync(
        Task registration,
        ExecutionScope scope,
        Func<CancellationToken, Task> executeAsync,
        CancellationToken cancellationToken
    )
    {
        await registration;
        using var executionScope = scope.Push();
        await executeAsync(cancellationToken);
    }
}
