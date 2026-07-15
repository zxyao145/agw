using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Agents.Store;
using Agw.Shared.Data.Repositories;

namespace Agw.Agents.Tests;

public class AgentRuntimeServiceDependencyTests
{
    [Fact]
    public void Constructor_DoesNotDependOnRepositories()
    {
        var constructor = Assert.Single(typeof(AgentRuntimeService).GetConstructors());
        var repositoryParameters = constructor.GetParameters()
            .Where(parameter => parameter.ParameterType.IsGenericType
                && parameter.ParameterType.GetGenericTypeDefinition() == typeof(IRepository<>))
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.Empty(repositoryParameters);
    }

    [Fact]
    public void Constructor_UsesAgentAppServiceForAgentDataAccess()
    {
        var constructor = Assert.Single(typeof(AgentRuntimeService).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(AgentAppService));
    }

    [Fact]
    public void Constructor_UsesAgentSessionStateStoreForSessionPersistence()
    {
        var constructor = Assert.Single(typeof(AgentRuntimeService).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(AgentSessionStateStore));
    }

    [Fact]
    public void Interface_UsesRequestObjectForChatMessageExecution()
    {
        var requestOverload = typeof(IAgentRuntimeService).GetMethod(
            nameof(IAgentRuntimeService.ExecuteByIdAsync),
            [typeof(AgentExecuteByIdRequest), typeof(CancellationToken)]
            );
        Assert.NotNull(requestOverload);
    }
}
