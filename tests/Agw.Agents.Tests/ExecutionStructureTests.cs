using Agw.Api.Controllers;

namespace Agw.Agents.Tests;

public class ExecutionStructureTests
{
    [Fact]
    public void ExecutionTypes_ShouldLiveOutsideControllerNamespace()
    {
        var executionTypeNames = new[]
        {
            "AgentExecutionCoordinator",
            "IAgentExecutionCoordinator",
            "ExecutionCommandContext",
            "ExecutionCommandDispatcher",
            "ExecutionConnectionState",
            "ExecutionInputTextExtractor",
            "SettingCommandStrategy",
            "ExecCommandStrategy",
            "InterruptCommandStrategy"
        };
        var assembly = typeof(AgentExecutionsController).Assembly;
        var executionTypes = executionTypeNames
            .Select(name => assembly.GetTypes().Single(type => type.Name == name))
            .ToArray();

        Assert.All(executionTypes, type => Assert.Equal("Agw.Api.Execution", type.Namespace));
    }
}
