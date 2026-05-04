using Agw.Agents.Application.Execution;
using Agw.Agents.Contracts;

namespace Agw.Agents.Tests;

public class ExecutionConnectionStateEnvironmentVariablesTests
{
    [Fact]
    public void ApplySettings_WhenEnvironmentVariablesChangedWhileIdle_RequiresImmediateSessionRefresh()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var originalSettings = CreateSettings(projectId, taskId, new Dictionary<string, string>
        {
            ["AGW_TOKEN"] = "one"
        });
        var changedSettings = CreateSettings(projectId, taskId, new Dictionary<string, string>
        {
            ["AGW_TOKEN"] = "two"
        });
        var state = new ExecutionConnectionState();

        state.ApplySettings(originalSettings);
        state.MarkSessionReady(originalSettings);
        state.ApplySettings(changedSettings);

        Assert.True(state.ShouldRefreshSessionImmediately);
        Assert.True(state.RequiresSessionRefreshBeforeNextExecution);
    }

    private static SettingCommand CreateSettings(
        Guid projectId,
        Guid taskId,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        return new SettingCommand(
            projectId,
            taskId,
            settingContent: "{}",
            environmentVariables: new Dictionary<string, string>(environmentVariables));
    }
}
