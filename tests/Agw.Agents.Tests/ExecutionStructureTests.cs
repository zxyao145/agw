using Agw.Api.Controllers;

namespace Agw.Agents.Tests;

public class ExecutionStructureTests
{
    [Fact]
    public void ExecutionTypes_ShouldLiveOutsideControllerNamespace()
    {
        var executionTypeNames = new[]
        {
            "ActiveTurn",
            "AgwUserInputUtil",
            "CommandDispatcher",
            "ExecutionCommandContext",
            "ExecutionConnectionState",
            "ExecutionCommandResult",
            "IExecutionCommandStrategy",
            "ExecCommandStrategy",
            "InterruptCommandStrategy",
            "SettingCommandStrategy"
        };
        var assembly = typeof(AgentExecutionsController).Assembly;
        var executionTypes = assembly
            .GetTypes()
            .Where(type => executionTypeNames.Contains(type.Name))
            .ToArray();

        Assert.Equal(executionTypeNames.Length, executionTypes.Length);
        Assert.All(executionTypes, type => Assert.StartsWith("Agw.Agents.Application.Execution", type.Namespace, StringComparison.Ordinal));
    }
}
