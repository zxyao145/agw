using Agw.Agents.Execution.Agents;

namespace Agw.Agents.Tests;

public class ExecutionStructureTests
{
    [Fact]
    public void ExecutionTypes_ShouldLiveInExecutionNamespaces()
    {
        var executionTypes = new Dictionary<string, string>
        {
            ["ExecutionCommandDispatcher"] = "Agw.Agents.Execution.Commands",
            ["ExecutionConnection"] = "Agw.Agents.Execution.Connections",
            ["ExecutionConnectionContext"] = "Agw.Agents.Execution.Connections",
            ["RuntimeFactory"] = "Agw.Agents.Execution.Runtimes",
            ["RuntimeBase"] = "Agw.Agents.Execution.Runtimes",
            ["AgentRuntime"] = "Agw.Agents.Execution.Runtimes",
            ["AgentflowRuntime"] = "Agw.Agents.Execution.Runtimes",
            ["ActiveTurn"] = "Agw.Agents.Execution.Turns",
            ["TurnPipeline"] = "Agw.Agents.Execution.Turns",
            ["RuntimeTurnContextAccessor"] = "Agw.Agents.Execution.Turns",
            ["ExecutionConnectionRegistry"] = "Agw.Agents.Execution.Transport.SignalR",
            ["ExecutionHub"] = "Agw.Agents.Execution.Transport.SignalR",
        };
        var assembly = typeof(AgentRuntimeService).Assembly;

        foreach (var (typeName, expectedNamespace) in executionTypes)
        {
            var executionType = Assert.Single(assembly.GetTypes(), type => type.Name == typeName);

            Assert.Equal(expectedNamespace, executionType.Namespace);
        }
    }

    [Fact]
    public void LegacyRuntimeTypes_ShouldNotRemain()
    {
        var assembly = typeof(AgentRuntimeService).Assembly;

        Assert.Null(assembly.GetType("Agw.Agents.Execution.RuntimeServiceBase"));
        Assert.Null(assembly.GetType("Agw.Agents.Execution.Runtimes.ExecutionStartResult"));
        Assert.Null(assembly.GetType("Agw.Agents.Execution.Runtimes.StreamingExecutionStartRequest"));
        Assert.DoesNotContain(
            assembly.GetTypes(),
            type => type.Namespace?.StartsWith("Agw.Agents.Runtime", StringComparison.Ordinal) == true
        );
    }

    [Fact]
    public void AgentflowsController_ShouldDependOnApplicationBoundary()
    {
        var assembly = typeof(AgentRuntimeService).Assembly;
        var controllerType = assembly.GetType("Agw.Agents.Definitions.Controllers.AgentflowsController");
        var appServiceType = assembly.GetType("Agw.Agents.Definitions.Agents.AgentflowAppService");
        var domainServiceType = assembly.GetType("Agw.Agents.Definitions.Domain.AgentflowDomainService");

        Assert.NotNull(controllerType);
        Assert.NotNull(appServiceType);
        Assert.NotNull(domainServiceType);

        var parameterTypes = Assert
            .Single(controllerType!.GetConstructors())
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(appServiceType!, parameterTypes);
        Assert.DoesNotContain(domainServiceType!, parameterTypes);
    }
}
