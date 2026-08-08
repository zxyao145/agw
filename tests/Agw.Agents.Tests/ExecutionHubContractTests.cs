using System.Reflection;

using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Transport.SignalR;
using Agw.Files.Exceptions;
using Agw.Shared.AgwMsgVm;

using Microsoft.AspNetCore.SignalR;

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

    [Fact]
    public async Task DispatchBoundary_AgwFilesException_UsesStableHubError()
    {
        var invokeAsync = typeof(ExecutionHub).GetMethod(
            "InvokeAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(invokeAsync);
        var action = new Func<Task>(() => Task.FromException(
            new AgwFilesException(
                FilesErrorCode.PathOutsideRoot,
                "Path is outside the project workspace.")));
        var invocation = Assert.IsAssignableFrom<Task>(invokeAsync.Invoke(null, [action]));

        var exception = await Assert.ThrowsAsync<HubException>(() => invocation);

        Assert.Equal("4030001: Path is outside the project workspace.", exception.Message);
    }
}
