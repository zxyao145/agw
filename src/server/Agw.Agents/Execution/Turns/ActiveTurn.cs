using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// The running execution turn and its cancellation source.
/// </summary>
public sealed class ActiveTurn : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Action? _interruptAction;
    private readonly Func<HumanResponseCommand, CancellationToken, ValueTask<bool>>? _submitHumanResponseAsync;
    private readonly Func<PermissionMode, CancellationToken, ValueTask>? _setPermissionModeAsync;

    public ActiveTurn(
        Task executionTask,
        CancellationTokenSource cancellationTokenSource,
        Action? interruptAction = null,
        Func<HumanResponseCommand, CancellationToken, ValueTask<bool>>? submitHumanResponseAsync = null,
        Func<PermissionMode, CancellationToken, ValueTask>? setPermissionModeAsync = null)
    {
        ExecutionTask = executionTask
            ?? throw new AgwException(ErrorCodes.InvalidParam, "executionTask cannot be null.");
        _cancellationTokenSource = cancellationTokenSource
            ?? throw new AgwException(ErrorCodes.InvalidParam, "cancellationTokenSource cannot be null.");
        _interruptAction = interruptAction;
        _submitHumanResponseAsync = submitHumanResponseAsync;
        _setPermissionModeAsync = setPermissionModeAsync;
    }

    public Task ExecutionTask { get; }

    public bool IsCompleted => ExecutionTask.IsCompleted;

    public void RequestInterrupt()
    {
        // Some runtimes need an explicit interruption hook in addition to cancellation.
        _interruptAction?.Invoke();

        if (_cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
    }

    public ValueTask<bool> TrySubmitHumanResponseAsync(
        HumanResponseCommand command,
        CancellationToken cancellationToken)
    {
        return _submitHumanResponseAsync?.Invoke(command, cancellationToken) ?? ValueTask.FromResult(false);
    }

    public async ValueTask<bool> TrySetPermissionModeAsync(
        PermissionMode permissionMode,
        CancellationToken cancellationToken)
    {
        if (_setPermissionModeAsync == null)
        {
            return false;
        }

        await _setPermissionModeAsync(permissionMode, cancellationToken);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        // Wait for the task to settle before releasing the linked cancellation source.
        try
        {
            await ExecutionTask;
        }
        catch (Exception)
        {
        }

        _cancellationTokenSource.Dispose();
    }
}
