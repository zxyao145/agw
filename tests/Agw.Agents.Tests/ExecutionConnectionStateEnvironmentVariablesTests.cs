using Agw.Agents.Application.Execution;
using Agw.Agents.Contracts;

namespace Agw.Agents.Tests;

public class ExecutionConnectionStateEnvironmentVariablesTests
{
    [Fact]
    public void ApplySettings_WhenEnvironmentVariablesChangedWhileIdle_RequiresImmediateSessionRefresh()
    {
        var projectId = Guid.NewGuid();
        var contextId = Guid.NewGuid().ToString("D");
        var originalSettings = CreateSettings(projectId, contextId, new Dictionary<string, string>
        {
            ["AGW_TOKEN"] = "one"
        });
        var changedSettings = CreateSettings(projectId, contextId, new Dictionary<string, string>
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
        string contextId,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        return new SettingCommand(
            projectId,
            environmentVariables: new Dictionary<string, string>(environmentVariables),
            contextId: contextId);
    }
}
