using Agw.Agents.Contracts;
using Agw.Shared.Data.Entities.Tasks;

namespace Agw.Agents.Application.Execution;

public sealed class ExecutionConnectionState
{
    private SettingCommand? _resolvedTaskSettings;

    /// <summary>
    /// Latest settings received from the client. These become the source of truth for the next execution request.
    /// </summary>
    public SettingCommand? CurrentSettings { get; private set; }

    /// <summary>
    /// Settings attached to the session currently running on the socket.
    /// When this differs from <see cref="CurrentSettings"/>, the next run needs a fresh session.
    /// </summary>
    public SettingCommand? SessionSettings { get; private set; }

    /// <summary>
    /// The active execution turn currently owned by the WebSocket connection.
    /// </summary>
    public ActiveTurn? ActiveExecution { get; private set; }

    /// <summary>
    /// Task resolved for the current settings. Reused while the SettingCommand is unchanged.
    /// </summary>
    public ProjectTask? ResolvedTask { get; private set; }

    /// <summary>
    /// A turn is considered running until its task has completed and been released by the controller loop.
    /// </summary>
    public bool HasRunningExecution => ActiveExecution is { IsCompleted: false };

    /// <summary>
    /// The current settings can reuse the existing session when both setting snapshots are identical.
    /// </summary>
    public bool HasReusableSessionForCurrentSettings =>
        CurrentSettings != null
        && SessionSettings != null
        && CurrentSettings == SessionSettings;

    /// <summary>
    /// A session refresh is required when the user has changed settings while a previous session still exists.
    /// </summary>
    public bool RequiresSessionRefreshBeforeNextExecution =>
        CurrentSettings != null
        && SessionSettings != null
        && CurrentSettings != SessionSettings;

    /// <summary>
    /// If no turn is running and settings changed, clear the old session before the next exec command starts.
    /// </summary>
    public bool ShouldRefreshSessionImmediately =>
        !HasRunningExecution && RequiresSessionRefreshBeforeNextExecution;

    public void ApplySettings(SettingCommand settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (CurrentSettings != null && CurrentSettings != settings)
        {
            ClearResolvedTask();
        }

        CurrentSettings = CloneSettings(settings);
    }

    public void MarkSessionReady(SettingCommand settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SessionSettings = CloneSettings(settings);
    }

    public void ClearSession()
    {
        // The session is disposed separately; this only resets the state snapshot used for reuse decisions.
        SessionSettings = null;
    }

    public bool TryGetResolvedTask(SettingCommand settings, out ProjectTask? task)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (ResolvedTask != null && _resolvedTaskSettings != null && _resolvedTaskSettings == settings)
        {
            task = ResolvedTask;
            return true;
        }

        task = null;
        return false;
    }

    public void MarkTaskResolved(SettingCommand settings, ProjectTask task)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(task);

        _resolvedTaskSettings = CloneSettings(settings);
        ResolvedTask = task;
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

        // Once the task is complete, dispose the turn so the next command can start from a clean slate.
        await ActiveExecution.DisposeAsync();
        ActiveExecution = null;
    }

    private static SettingCommand CloneSettings(SettingCommand settings)
    {
        return new SettingCommand(
            settings.ProjectId,
            settings.TaskId,
            settings.Workspace,
            settings.SettingContent,
            new Dictionary<string, string>(settings.EnvironmentVariables))
        {
            Resume = settings.Resume
        };
    }

    private void ClearResolvedTask()
    {
        _resolvedTaskSettings = null;
        ResolvedTask = null;
    }
}
