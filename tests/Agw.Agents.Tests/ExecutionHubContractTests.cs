using Agw.Agents.Contracts;
using Agw.Agents.Hubs;
using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Tests;

public class ExecutionHubContractTests
{
    [Fact]
    public void Hub_ExposesOnlyCommandDispatchForExecutionControl()
    {
        var publicMethods = typeof(ExecutionHub)
            .GetMethods()
            .Where(method => method.DeclaringType == typeof(ExecutionHub))
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains(nameof(ExecutionHub.DispatchCommand), publicMethods);
        Assert.DoesNotContain("StartExecution", publicMethods);
        Assert.DoesNotContain("AttachExecution", publicMethods);
        Assert.DoesNotContain("InterruptExecution", publicMethods);
    }

    [Fact]
    public void DispatchCommand_AcceptsPolymorphicAgentRunCommand()
    {
        var parameter = typeof(ExecutionHub)
            .GetMethod(nameof(ExecutionHub.DispatchCommand))!
            .GetParameters()
            .Single();

        Assert.Equal(typeof(AgentRunCommand), parameter.ParameterType);
    }

    [Fact]
    public void ClientContract_ReceivesRawAgwMessage()
    {
        var parameter = typeof(IExecutionHubClient)
            .GetMethod(nameof(IExecutionHubClient.ReceiveMessage))!
            .GetParameters()
            .Single();

        Assert.Equal(typeof(AgwMessage), parameter.ParameterType);
    }
}
