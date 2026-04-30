using Agw.Api.Controllers;

namespace Agw.Agents.Tests;

public class ExecutionStructureTests
{
    [Fact]
    public void ExecutionTypes_ShouldLiveInApplicationExecutionNamespaces()
    {
        var executionTypes = new Dictionary<string, string>
        {
            ["ActiveTurn"] = "Agw.Agents.Application.Execution",
            ["AgwUserInputUtil"] = "Agw.Agents.Application.Execution",
            ["CommandDispatcher"] = "Agw.Agents.Application.Execution",
            ["ExecutionCommandContext"] = "Agw.Agents.Application.Execution",
            ["ExecutionConnectionState"] = "Agw.Agents.Application.Execution",
            ["ExecCommandStrategy"] = "Agw.Agents.Application.Execution.CommandStrategies",
            ["IExecutionCommandStrategy"] = "Agw.Agents.Application.Execution.CommandStrategies",
            ["InterruptCommandStrategy"] = "Agw.Agents.Application.Execution.CommandStrategies",
            ["SettingCommandStrategy"] = "Agw.Agents.Application.Execution.CommandStrategies",
        };
        var assembly = typeof(AgentExecutionsController).Assembly;

        foreach (var (typeName, expectedNamespace) in executionTypes)
        {
            var executionType = Assert.Single(assembly.GetTypes(), type => type.Name == typeName);

            Assert.Equal(expectedNamespace, executionType.Namespace);
        }
    }
}
