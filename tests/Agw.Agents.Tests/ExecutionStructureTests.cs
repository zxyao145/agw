using Agw.Agents.Execution.Agents.Runtime;

namespace Agw.Agents.Tests;

public class ExecutionStructureTests
{
    [Fact]
    public void ExecutionTypes_ShouldLiveInExecutionNamespaces()
    {
        var executionTypes = new Dictionary<string, string>
        {
            ["AgentRuntimeFactory"] = "Agw.Agents.Execution.Agents.Runtime",
            ["AgentflowRuntimeFactory"] = "Agw.Agents.Execution.Agentflows.Runtime",
            ["AgentTurnExecutor"] = "Agw.Agents.Execution.Agents.Turns",
            ["AgentflowTurnExecutor"] = "Agw.Agents.Execution.Agentflows.Turns",
            ["AgentflowWorkflowCompiler"] = "Agw.Agents.Execution.Agentflows.Workflows",
            ["AgentflowCheckpointStore"] = "Agw.Agents.Execution.Agentflows.Checkpoints",
            ["DurableAgentflowCheckpointStore"] = "Agw.Agents.Execution.Agentflows.Checkpoints.Durable",
            ["DurableAgentflowCheckpoint"] = "Agw.Agents.Execution.Agentflows.Checkpoints.Durable",
            ["IInteractionHandler"] = "Agw.Agents.Execution.HumanInteraction.Application",
            ["InProcessInteractionSession"] = "Agw.Agents.Execution.HumanInteraction.InProcess",
            ["PendingInteractionHandler"] = "Agw.Agents.Execution.HumanInteraction.Application",
            ["PendingInteractionSet"] = "Agw.Agents.Execution.HumanInteraction.Application",
            ["InMemoryPendingInteractionSet"] = "Agw.Agents.Execution.HumanInteraction.InProcess",
            ["DurablePendingInteractionSet"] = "Agw.Agents.Execution.HumanInteraction.Durable",
            ["InteractionRules"] = "Agw.Agents.Execution.HumanInteraction.Application",
            ["HumanInteractionContextAccessor"] = "Agw.Agents.Execution.HumanInteraction",
            ["ResolvedHumanInteractionChannel"] = "Agw.Agents.Execution.HumanInteraction.Application",
            ["MafApprovalBatchAgent"] = "Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf",
            ["MafApprovalGrantAgent"] = "Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf",
            ["MafApprovalAdapter"] = "Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf",
            ["IExecutionCoordinator"] = "Agw.Agents.Execution.Runtimes.Contracts",
            ["InProcessExecutionCoordinator"] = "Agw.Agents.Execution.Runtimes.InProcess",
            ["InProcessTurnHost"] = "Agw.Agents.Execution.Runtimes.InProcess",
            ["ActiveTurn"] = "Agw.Agents.Execution.Runtimes.InProcess",
            ["DurableExecutionCoordinator"] = "Agw.Agents.Execution.Runtimes.Durable",
            ["DurableExecutionAttachment"] = "Agw.Agents.Execution.Runtimes.Durable",
            ["DistributedExecutionWorker"] = "Agw.Agents.Execution.Runtimes.Durable",
            ["DurableExecutionStore"] = "Agw.Agents.Execution.Persistence.Durable",
            ["IExecutionMessageSink"] = "Agw.Agents.Execution.Outbound",
            ["IExecutionHubClient"] = "Agw.Agents.Execution.Outbound.SignalR",
            ["SignalRExecutionMessageSink"] = "Agw.Agents.Execution.Outbound.SignalR",
            ["DurableEventSink"] = "Agw.Agents.Execution.Outbound.Durable",
            ["DurableExecutionEventLog"] = "Agw.Agents.Execution.Messaging.Durable",
            ["RedisExecutionEventProjection"] = "Agw.Agents.Execution.Messaging.Durable",
            ["DurableSegmentScheduler"] = "Agw.Agents.Execution.Runtimes.Durable",
            ["TurnBroadcast"] = "Agw.Agents.Execution.Turns",
            ["TurnRecord"] = "Agw.Agents.Execution.Turns",
            ["ExecutionRuntimeOptions"] = "Agw.Agents.Execution.Configuration",
            ["ExecutionCommandDispatcher"] = "Agw.Agents.Execution.Commands",
            ["ExecutionConnection"] = "Agw.Agents.Execution.Inbound.Connections",
            ["ExecutionConnectionContext"] = "Agw.Agents.Execution.Inbound.Connections",
            ["ExecutionConnectionContextFactory"] = "Agw.Agents.Execution.Inbound.Connections",
            ["ExecutionSettings"] = "Agw.Agents.Execution.Runtimes",
            ["ExecutionTarget"] = "Agw.Agents.Execution.Inbound.Connections",
            ["AgentExecutionFacade"] = "Agw.Agents.Execution.Inbound.Facades",
            ["TurnAcceptanceService"] = "Agw.Agents.Execution.Turns",
            ["AgentRuntime"] = "Agw.Agents.Execution.Agents.Runtime",
            ["AgentflowRuntime"] = "Agw.Agents.Execution.Agentflows.Runtime",
            ["TurnPipeline"] = "Agw.Agents.Execution.Turns",
            ["ExecutionScope"] = "Agw.Agents.Execution.Context",
            ["AgentExecutionContextAccessor"] = "Agw.Agents.Execution.Context",
            ["ExecutionConnectionRegistry"] = "Agw.Agents.Execution.Inbound.SignalR",
            ["ExecutionHub"] = "Agw.Agents.Execution.Inbound.SignalR",
        };
        var assembly = typeof(AgentRuntimeFactory).Assembly;

        foreach (var (typeName, expectedNamespace) in executionTypes)
        {
            var executionType = Assert.Single(assembly.GetTypes(), type => type.Name == typeName);

            Assert.Equal(expectedNamespace, executionType.Namespace);
        }
    }

    [Fact]
    public void LegacyRuntimeTypes_ShouldNotRemain()
    {
        var assembly = typeof(AgentRuntimeFactory).Assembly;

        Assert.Null(assembly.GetType("Agw.Agents.Execution.RuntimeServiceBase"));
        Assert.Null(assembly.GetType("Agw.Agents.Execution.Runtimes.ExecutionStartResult"));
        Assert.Null(assembly.GetType("Agw.Agents.Execution.Runtimes.StreamingExecutionStartRequest"));
        Assert.DoesNotContain(
            assembly.GetTypes(),
            type => type.Namespace?.StartsWith("Agw.Agents.Runtime", StringComparison.Ordinal) == true
        );
    }

    [Fact]
    public void ExecutionTypeNames_UseLayerSuffixes()
    {
        var assembly = typeof(AgentRuntimeFactory).Assembly;

        Assert.DoesNotContain(
            assembly.GetTypes(),
            type =>
                type.Name.EndsWith("RuntimeService", StringComparison.Ordinal)
                || type.Name.EndsWith("Runner", StringComparison.Ordinal)
                || type.Name.EndsWith("Starter", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void AgentflowsController_ShouldDependOnApplicationBoundary()
    {
        var assembly = typeof(Agw.Agents.Definitions.Agents.AgentflowAppService).Assembly;
        Assert.NotEqual(assembly, typeof(AgentRuntimeFactory).Assembly);
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
        Assert.DoesNotContain(parameterTypes, type => type.Assembly == typeof(AgentRuntimeFactory).Assembly);
    }
}
