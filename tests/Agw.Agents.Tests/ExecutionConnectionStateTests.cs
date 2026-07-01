using Agw.Agents.Application.Execution;
using Agw.Agents.Contracts;
using Agw.Shared.Contracts.Tasks;

namespace Agw.Agents.Tests;

public class ExecutionConnectionStateTests
{
    [Fact]
    public void ApplySettings_WhenSettingsChangedWhileExecutionRunning_DefersImmediateSessionRefresh()
    {
        var originalSettings = CreateSettings(taskId: Guid.NewGuid());
        var changedSettings = CreateSettings(taskId: Guid.NewGuid());
        using var executionCts = new CancellationTokenSource();
        var pendingExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ExecutionConnectionState();

        state.ApplySettings(originalSettings);
        state.MarkSessionReady(originalSettings);
        state.TryStartExecution(new ActiveTurn(pendingExecution.Task, executionCts));

        state.ApplySettings(changedSettings);

        Assert.True(state.HasRunningExecution);
        Assert.False(state.ShouldRefreshSessionImmediately);
        Assert.True(state.RequiresSessionRefreshBeforeNextExecution);
    }

    [Fact]
    public void ApplySettings_WhenSettingsChangedWhileIdle_RequiresImmediateSessionRefresh()
    {
        var originalSettings = CreateSettings(taskId: Guid.NewGuid());
        var changedSettings = CreateSettings(taskId: Guid.NewGuid());
        var state = new ExecutionConnectionState();

        state.ApplySettings(originalSettings);
        state.MarkSessionReady(originalSettings);

        state.ApplySettings(changedSettings);

        Assert.False(state.HasRunningExecution);
        Assert.True(state.ShouldRefreshSessionImmediately);
        Assert.True(state.RequiresSessionRefreshBeforeNextExecution);
    }

    [Fact]
    public void ApplySettings_WhenSettingsUnchanged_KeepsSessionReusable()
    {
        var projectId = Guid.NewGuid();
        var settings = CreateSettings(projectId, Guid.NewGuid());
        var state = new ExecutionConnectionState();

        state.ApplySettings(settings);
        state.MarkSessionReady(settings);
        state.ApplySettings(CreateSettings(projectId, settings.TaskId));

        Assert.False(state.ShouldRefreshSessionImmediately);
        Assert.False(state.RequiresSessionRefreshBeforeNextExecution);
    }

    [Fact]
    public void TryStartExecution_WhenAnotherExecutionIsRunning_ReturnsFalse()
    {
        using var firstCts = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();
        var firstExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ExecutionConnectionState();

        var firstStarted = state.TryStartExecution(new ActiveTurn(firstExecution.Task, firstCts));
        var secondStarted = state.TryStartExecution(new ActiveTurn(Task.CompletedTask, secondCts));

        Assert.True(firstStarted);
        Assert.False(secondStarted);
        Assert.True(state.HasRunningExecution);
    }

    [Fact]
    public async Task ReleaseCompletedExecutionAsync_WhenExecutionCompleted_ClearsActiveExecution()
    {
        using var executionCts = new CancellationTokenSource();
        var state = new ExecutionConnectionState();

        state.TryStartExecution(new ActiveTurn(Task.CompletedTask, executionCts));

        await state.ReleaseCompletedExecutionAsync();

        Assert.False(state.HasRunningExecution);
        Assert.Null(state.ActiveExecution);
    }

    [Fact]
    public void TryGetResolvedTask_WhenSettingsUnchanged_ReturnsCachedTask()
    {
        var projectId = Guid.NewGuid();
        var settings = CreateSettings(projectId, Guid.NewGuid());
        var equivalentSettings = CreateSettings(projectId, settings.TaskId);
        var task = new TaskProjection { TaskId = settings.TaskId, ProjectId = projectId };
        var state = new ExecutionConnectionState();

        state.ApplySettings(settings);
        state.MarkTaskResolved(settings, task);

        var found = state.TryGetResolvedTask(equivalentSettings, out var cachedTask);

        Assert.True(found);
        Assert.Same(task, cachedTask);
    }

    [Fact]
    public void ApplySettings_WhenSettingsChanged_ClearsCachedTask()
    {
        var originalSettings = CreateSettings(taskId: Guid.NewGuid());
        var changedSettings = CreateSettings(taskId: Guid.NewGuid());
        var task = new TaskProjection { TaskId = originalSettings.TaskId, ProjectId = originalSettings.ProjectId };
        var state = new ExecutionConnectionState();

        state.ApplySettings(originalSettings);
        state.MarkTaskResolved(originalSettings, task);

        state.ApplySettings(changedSettings);

        Assert.Null(state.ResolvedTask);
        Assert.False(state.TryGetResolvedTask(originalSettings, out _));
    }

    private static SettingCommand CreateSettings(Guid taskId)
    {
        return CreateSettings(Guid.NewGuid(), taskId);
    }

    private static SettingCommand CreateSettings(Guid projectId, Guid taskId)
    {
        return new SettingCommand(
            projectId: projectId,
            taskId: taskId);
    }
}
