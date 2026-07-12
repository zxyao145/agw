using Agw.Agents.Hubs;

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
            ["ExecutionRuntimeStarter"] = "Agw.Agents.Application.Execution",
            ["ExecutionTurnRunner"] = "Agw.Agents.Application.Execution",
            ["HubExecutionConnectionRegistry"] = "Agw.Agents.Application.Execution",
            ["RuntimeExecSessionBase"] = "Agw.Agents.Application.Execution",
        };
        var assembly = typeof(ExecutionHub).Assembly;

        foreach (var (typeName, expectedNamespace) in executionTypes)
        {
            var executionType = Assert.Single(assembly.GetTypes(), type => type.Name == typeName);

            Assert.Equal(expectedNamespace, executionType.Namespace);
        }
    }
}
