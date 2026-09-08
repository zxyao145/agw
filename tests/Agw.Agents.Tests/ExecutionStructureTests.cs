using Agw.Agents.Execution.Agents.Runtime;

namespace Agw.Agents.Tests;

public class ExecutionStructureTests
{
    [Fact]
    public void ExecutionTypes_ShouldLiveInExecutionNamespaces()
    {
        var executionTypes = new Dictionary<string, string>
        {
            ["AgentRuntimeService"] = "Agw.Agents.Execution.Agents.Runtime",
            ["AgentflowRuntimeService"] = "Agw.Agents.Execution.Agentflows.Runtime",
            ["DurableAgentSegmentRunner"] = "Agw.Agents.Execution.Agents.Runners.Durable",
            ["InProcessAgentflowRunner"] = "Agw.Agents.Execution.Agentflows.Runners.InProcess",
            ["DurableAgentflowSegmentRunner"] = "Agw.Agents.Execution.Agentflows.Runners.Durable",
            ["AgentflowWorkflowCompiler"] = "Agw.Agents.Execution.Agentflows.Workflows",
            ["AgentflowCheckpointStore"] = "Agw.Agents.Execution.Agentflows.Checkpoints",
            ["DurableAgentflowCheckpointStore"] = "Agw.Agents.Execution.Agentflows.Checkpoints.Durable",
            ["DurableAgentflowCheckpoint"] = "Agw.Agents.Execution.Agentflows.Checkpoints.Durable",
            ["HumanGateApprovalRequest"] = "Agw.Agents.Execution.HumanInteraction.Contracts",
            ["IHumanGateApprovalHandler"] = "Agw.Agents.Execution.HumanInteraction.Contracts",
            ["HumanInteractionContextAccessor"] = "Agw.Agents.Execution.HumanInteraction",
            ["PermissionAwareApprovalHandler"] = "Agw.Agents.Execution.HumanInteraction.Approvals",
            ["ExecutionHumanInteractionChannel"] = "Agw.Agents.Execution.HumanInteraction.InProcess",
            ["HumanGateApprovalCoordinator"] = "Agw.Agents.Execution.HumanInteraction.InProcess",
            ["ResolvedHumanInteractionChannel"] = "Agw.Agents.Execution.HumanInteraction.Durable",
            ["DurableHumanInteractionMapper"] = "Agw.Agents.Execution.HumanInteraction.Durable",
            ["DurableHumanInteractionSnapshot"] = "Agw.Agents.Execution.HumanInteraction.Durable.Contracts",
            ["DurableExecutionCoordinator"] = "Agw.Agents.Execution.Runtimes.Durable",
            ["DistributedExecutionWorker"] = "Agw.Agents.Execution.Runtimes.Durable",
            ["IDurableExecutionClient"] = "Agw.Agents.Execution.Runtimes.Durable.Contracts",
            ["DurableExecutionStore"] = "Agw.Agents.Execution.Persistence.Durable",
            ["IExecutionMessageSink"] = "Agw.Agents.Execution.Outbound",
            ["IExecutionHubClient"] = "Agw.Agents.Execution.Outbound.SignalR",
            ["SignalRExecutionMessageSink"] = "Agw.Agents.Execution.Outbound.SignalR",
            ["ExecutionStreamMessageSink"] = "Agw.Agents.Execution.Outbound.Durable",
            ["IExecutionEventStream"] = "Agw.Agents.Execution.Messaging.Durable",
            ["ExecutionRuntimeOptions"] = "Agw.Agents.Execution.Configuration",
            ["ExecutionCommandDispatcher"] = "Agw.Agents.Execution.Commands",
            ["ExecutionConnection"] = "Agw.Agents.Execution.Inbound.Connections",
            ["ExecutionConnectionContext"] = "Agw.Agents.Execution.Inbound.Connections",
            ["ExecutionConnectionContextFactory"] = "Agw.Agents.Execution.Inbound.Connections",
            ["ExecutionSettings"] = "Agw.Agents.Execution.Inbound.Connections",
            ["ExecutionTarget"] = "Agw.Agents.Execution.Inbound.Connections",
            ["AgentExecutionFacade"] = "Agw.Agents.Execution.Inbound.Facades",
            ["RuntimeFactory"] = "Agw.Agents.Execution.Runtimes.InProcess",
            ["RuntimeBase"] = "Agw.Agents.Execution.Runtimes",
            ["AgentRuntime"] = "Agw.Agents.Execution.Agents.Runtime",
            ["AgentflowRuntime"] = "Agw.Agents.Execution.Agentflows.Runtime",
            ["ActiveTurn"] = "Agw.Agents.Execution.Turns",
            ["TurnPipeline"] = "Agw.Agents.Execution.Turns",
            ["RuntimeTurnContextAccessor"] = "Agw.Agents.Execution.Turns",
            ["ExecutionConnectionRegistry"] = "Agw.Agents.Execution.Inbound.SignalR",
            ["ExecutionHub"] = "Agw.Agents.Execution.Inbound.SignalR",
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
        var assembly = typeof(Agw.Agents.Definitions.Agents.AgentflowAppService).Assembly;
        Assert.NotEqual(assembly, typeof(AgentRuntimeService).Assembly);
        var controllerType = assembly.GetType("Agw.Agents.Definitions.Controllers.AgentflowsController");
        var appServiceType = assembly.GetType("Agw.Agents.Definitions.Agents.AgentflowAppService");
        var legacyDomainServiceType = assembly.GetType("Agw.Agents.Definitions.Domain.AgentflowDomainService");

        Assert.NotNull(controllerType);
        Assert.NotNull(appServiceType);
        Assert.Null(legacyDomainServiceType);

        var parameterTypes = Assert
            .Single(controllerType!.GetConstructors())
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(appServiceType!, parameterTypes);
        Assert.Contains(typeof(Agw.Agents.Contracts.Catalog.IAgentflowMermaidProvider), parameterTypes);
        Assert.DoesNotContain(parameterTypes, type => type.Assembly == typeof(AgentRuntimeService).Assembly);
    }
}
