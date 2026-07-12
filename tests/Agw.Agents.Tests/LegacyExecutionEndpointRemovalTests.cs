using Agw.Agents.Runtime.Hubs;

using Microsoft.AspNetCore.Mvc.Routing;

namespace Agw.Agents.Tests;

public class LegacyExecutionEndpointRemovalTests
{
    [Fact]
    public void AgentsAssembly_DoesNotExposeLegacyExecutionRoutes()
    {
        var routeTemplates = typeof(ExecutionHub).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods())
            .SelectMany(method => method.GetCustomAttributes(typeof(HttpMethodAttribute), inherit: true))
            .Cast<HttpMethodAttribute>()
            .Select(attribute => attribute.Template)
            .ToArray();

        Assert.DoesNotContain("{agentId:guid}/ws", routeTemplates);
        Assert.DoesNotContain("{id:guid}/execute", routeTemplates);
    }

    [Theory]
    [InlineData("Agw.Api.Controllers.AgentExecutionsController")]
    [InlineData("Agw.Agents.Application.Execution.CommandDispatcher")]
    [InlineData("Agw.Agents.Application.Execution.ExecutionCommandContext")]
    [InlineData("Agw.Agents.Application.Execution.ExecutionConnectionState")]
    [InlineData("Agw.Agents.Application.Execution.CommandStrategies.ExecCommandStrategy")]
    [InlineData("Agw.Agents.Application.Execution.CommandStrategies.HumanResponseCommandStrategy")]
    [InlineData("Agw.Agents.Application.Execution.CommandStrategies.InterruptCommandStrategy")]
    [InlineData("Agw.Agents.Application.Execution.CommandStrategies.SettingCommandStrategy")]
    public void AgentsAssembly_DoesNotContainLegacyExecutionTypes(string typeName)
    {
        Assert.Null(typeof(ExecutionHub).Assembly.GetType(typeName));
    }
}
