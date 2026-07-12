using Agw.Agents.Runtime.Hubs;

namespace Agw.Agents.Tests;

public class ExecutionStructureTests
{
    [Fact]
    public void ExecutionTypes_ShouldLiveInApplicationExecutionNamespaces()
    {
        var executionTypes = new Dictionary<string, string>
        {
            ["ActiveTurn"] = "Agw.Agents.Runtime.Execution",
            ["AgwUserInputUtil"] = "Agw.Agents.Runtime.Execution",
            ["ExecutionRuntimeStarter"] = "Agw.Agents.Runtime.Execution",
            ["ExecutionTurnRunner"] = "Agw.Agents.Runtime.Execution",
            ["HubExecutionConnectionRegistry"] = "Agw.Agents.Runtime.Execution",
            ["RuntimeExecSessionBase"] = "Agw.Agents.Runtime.Execution",
        };
        var assembly = typeof(ExecutionHub).Assembly;

        foreach (var (typeName, expectedNamespace) in executionTypes)
        {
            var executionType = Assert.Single(assembly.GetTypes(), type => type.Name == typeName);

            Assert.Equal(expectedNamespace, executionType.Namespace);
        }
    }

    [Fact]
    public void AgentflowsController_ShouldDependOnApplicationBoundary()
    {
        var assembly = typeof(ExecutionHub).Assembly;
        var controllerType = assembly.GetType("Agw.Agents.Definitions.Controllers.AgentflowsController");
        var appServiceType = assembly.GetType("Agw.Agents.Definitions.Agents.AgentflowAppService");
        var domainServiceType = assembly.GetType("Agw.Agents.Definitions.Domain.AgentflowDomainService");

        Assert.NotNull(controllerType);
        Assert.NotNull(appServiceType);
        Assert.NotNull(domainServiceType);

        var parameterTypes = Assert.Single(controllerType!.GetConstructors())
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(appServiceType!, parameterTypes);
        Assert.DoesNotContain(domainServiceType!, parameterTypes);
    }
}
