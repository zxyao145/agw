using Agw.Agents.Application.Execution;
using Agw.Agents.Contracts;
using Agw.Shared.Contracts.Tasks;

namespace Agw.Agents.Tests;

public class ExecutionConnectionStateTests
{
    [Fact]
    public void ApplySettings_WhenSettingsChangedWhileExecutionRunning_DefersImmediateSessionRefresh()
    {
        var originalSettings = CreateSettings(contextId: Guid.NewGuid().ToString("D"));
        var changedSettings = CreateSettings(contextId: Guid.NewGuid().ToString("D"));
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
        var originalSettings = CreateSettings(contextId: Guid.NewGuid().ToString("D"));
        var changedSettings = CreateSettings(contextId: Guid.NewGuid().ToString("D"));
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
        var contextId = Guid.NewGuid().ToString("D");
        var settings = CreateSettings(projectId, contextId);
        var state = new ExecutionConnectionState();

        state.ApplySettings(settings);
        state.MarkSessionReady(settings);
        state.ApplySettings(CreateSettings(projectId, contextId));

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
        var contextId = Guid.NewGuid().ToString("D");
        var taskId = Guid.NewGuid();
        var settings = CreateSettings(projectId, contextId);
        var equivalentSettings = CreateSettings(projectId, contextId);
        var task = new TaskProjection { TaskId = taskId, ProjectId = projectId, ContextId = contextId };
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
        var originalSettings = CreateSettings(contextId: Guid.NewGuid().ToString("D"));
        var changedSettings = CreateSettings(contextId: Guid.NewGuid().ToString("D"));
        var task = new TaskProjection
        {
            TaskId = Guid.NewGuid(),
            ProjectId = originalSettings.ProjectId,
            ContextId = originalSettings.ContextId!
        };
        var state = new ExecutionConnectionState();

        state.ApplySettings(originalSettings);
        state.MarkTaskResolved(originalSettings, task);

        state.ApplySettings(changedSettings);

        Assert.Null(state.ResolvedTask);
        Assert.False(state.TryGetResolvedTask(originalSettings, out _));
    }

    private static SettingCommand CreateSettings(string contextId)
    {
        return CreateSettings(Guid.NewGuid(), contextId);
    }

    private static SettingCommand CreateSettings(Guid projectId, string contextId)
    {
        return new SettingCommand(
            projectId: projectId,
            contextId: contextId);
    }
}
