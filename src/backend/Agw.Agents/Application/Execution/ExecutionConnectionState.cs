using Agw.Agents.Contracts;

namespace Agw.Agents.Application.Execution;

public sealed class ExecutionConnectionState
{
    /// <summary>
    /// The latest SettingCommand
    /// </summary>
    public SettingCommand? CurrentSettings { get; private set; }

    /// <summary>
    /// The SettingCommand of the currently executing session
    /// </summary>
    public SettingCommand? SessionSettings { get; private set; }

    public ActiveTurn? ActiveExecution { get; private set; }

    public bool HasRunningExecution => ActiveExecution is { IsCompleted: false };

    public bool HasReusableSessionForCurrentSettings =>
        CurrentSettings != null
        && SessionSettings != null
        && CurrentSettings == SessionSettings;

    public bool RequiresSessionRefreshBeforeNextExecution =>
        CurrentSettings != null
        && SessionSettings != null
        && CurrentSettings != SessionSettings;

    public bool ShouldRefreshSessionImmediately =>
        !HasRunningExecution && RequiresSessionRefreshBeforeNextExecution;

    public void ApplySettings(SettingCommand settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CurrentSettings = CloneSettings(settings);
    }

    public void MarkSessionReady(SettingCommand settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SessionSettings = CloneSettings(settings);
    }

    public void ClearSession()
    {
        SessionSettings = null;
    }

    public bool TryStartExecution(ActiveTurn activeExecution)
    {
        ArgumentNullException.ThrowIfNull(activeExecution);

        if (HasRunningExecution)
        {
            return false;
        }

        ActiveExecution = activeExecution;
        return true;
    }

    public async Task ReleaseCompletedExecutionAsync()
    {
        if (ActiveExecution == null || !ActiveExecution.IsCompleted)
        {
            return;
        }

        await ActiveExecution.DisposeAsync();
        ActiveExecution = null;
    }

    private static SettingCommand CloneSettings(SettingCommand settings)
    {
        return new SettingCommand(settings.ProjectId, settings.TaskId, settings.Workspace, settings.SettingContent)
        {
            Resume = settings.Resume
        };
    }
}

/// <summary>
/// The turn currently being processed by the session
/// </summary>
/// <param name="executionTask"></param>
/// <param name="cancellationTokenSource"></param>
/// <param name="interruptAction"></param>
public sealed class ActiveTurn(
    Task executionTask,
    CancellationTokenSource cancellationTokenSource,
    Action? interruptAction = null) : IAsyncDisposable
{
    public Task ExecutionTask { get; } = executionTask ?? throw new ArgumentNullException(nameof(executionTask));

    public bool InterruptRequested { get; private set; }

    public bool IsCompleted => ExecutionTask.IsCompleted;

    private readonly CancellationTokenSource _cancellationTokenSource =
        cancellationTokenSource ?? throw new ArgumentNullException(nameof(cancellationTokenSource));

    private readonly Action? _interruptAction = interruptAction;

    public void RequestInterrupt(string? reason)
    {
        InterruptRequested = true;
        _interruptAction?.Invoke();

        if (_cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
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
