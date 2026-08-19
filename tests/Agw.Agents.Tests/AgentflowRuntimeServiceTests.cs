using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Summaries;
using Agw.Shared;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Tests;

[Collection(AgentflowExecutionTraceTestCollection.Name)]
public class AgentflowRuntimeServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ToolApprovalRequest_FailsUnattendedExecution()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "approval-flow" };
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Worker",
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "agent-output",
                SourceNodeId = "agent",
                TargetNodeId = "output",
            },
        };
        var service = new AgentflowRuntimeService(
            NullLogger<AgentflowRuntimeService>.Instance,
            new TestRepository<Agentflow>([agentflow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            new AgentflowDomainService(TimeProvider.System),
            new StubAgentRuntimeService(_ => new ApprovalRequestAgent()),
            new StubProviderSessionState(),
            new RecordingSummaryService()
        );

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await service.ExecuteAsync(
                agentflow.Id,
                Guid.CreateVersion7(),
                "run",
                TestContext.Current.CancellationToken,
                Guid.CreateVersion7(),
                "unattended-context"
            )
        );

        Assert.Contains("unattended Agentflow execution", exception.Message);
    }

    [Theory]
    [InlineData("once")]
    [InlineData("always-tool")]
    [InlineData("always-arguments")]
    public async Task ExecuteStreamingAsync_ToolApprovalRequest_ResumesThroughWorkflowResponse(string approvalScope)
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "approval-flow" };
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Worker",
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "agent-output",
                SourceNodeId = "agent",
                TargetNodeId = "output",
            },
        };
        var service = new AgentflowRuntimeService(
            NullLogger<AgentflowRuntimeService>.Instance,
            new TestRepository<Agentflow>([agentflow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            new AgentflowDomainService(TimeProvider.System),
            new StubAgentRuntimeService(_ => new ApprovalRequestAgent()),
            new StubProviderSessionState(),
            new RecordingSummaryService()
        );
        var messages = new List<AgwMessage>();

        await foreach (
            var message in service.ExecuteStreamingAsync(
                agentflow.Id,
                "run",
                TestContext.Current.CancellationToken,
                Guid.CreateVersion7(),
                "interactive-context",
                Guid.CreateVersion7(),
                new DelayedApprovalHandler(approvalScope)
            )
        )
        {
            messages.Add(message);
        }

        Assert.Single(
            messages,
            message =>
                message.AdditionalProperties?.TryGetValue("type", out var type) == true
                && string.Equals(type?.ToString(), "tool-approval-request", StringComparison.Ordinal)
        );
        Assert.Contains(
            messages.SelectMany(message => message.Contents).OfType<AgwTextContent>(),
            content => content.Content == approvalScope
        );
    }

    [Fact]
    public async Task GetMermaidAsync_AgentCreationFails_DisposesAlreadyCreatedAgents()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "creation-failure" };
        var firstAgentId = Guid.CreateVersion7();
        var secondAgentId = Guid.CreateVersion7();
        var firstAgent = new TrackingAIAgent();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "first",
                Kind = AgentflowNodeKind.Agent,
                RelateId = firstAgentId,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "second",
                Kind = AgentflowNodeKind.Agent,
                RelateId = secondAgentId,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "first-second",
                SourceNodeId = "first",
                TargetNodeId = "second",
            },
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "second-output",
                SourceNodeId = "second",
                TargetNodeId = "output",
            },
        };
        var service = new AgentflowRuntimeService(
            NullLogger<AgentflowRuntimeService>.Instance,
            new TestRepository<Agentflow>([agentflow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            new AgentflowDomainService(TimeProvider.System),
            new StubAgentRuntimeService(agentId => agentId == firstAgentId ? firstAgent : null),
            new StubProviderSessionState(),
            new RecordingSummaryService()
        );

        var mermaid = await service.GetMermaidAsync(agentflow.Id, TestContext.Current.CancellationToken);

        Assert.Null(mermaid);
        Assert.True(firstAgent.Disposed);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_CheckpointMarker_EmitsNamedMafCheckpoint()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "checkpoint-flow" };
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "checkpoint",
                Kind = AgentflowNodeKind.CheckpointMarker,
                Name = "Fallback Name",
                ConfigJson = """{"checkpointName":"Review Ready"}""",
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "agent-checkpoint",
                SourceNodeId = "agent",
                TargetNodeId = "checkpoint",
            },
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "checkpoint-output",
                SourceNodeId = "checkpoint",
                TargetNodeId = "output",
            },
        };
        var logger = new CapturingLogger<AgentflowRuntimeService>();
        var service = new AgentflowRuntimeService(
            logger,
            new TestRepository<Agentflow>([agentflow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            new AgentflowDomainService(TimeProvider.System),
            new StubAgentRuntimeService(agentId),
            new StubProviderSessionState(),
            new RecordingSummaryService()
        );

        await foreach (
            var _ in service.ExecuteStreamingAsync(
                agentflow.Id,
                "run",
                TestContext.Current.CancellationToken,
                taskId: Guid.CreateVersion7()
            )
        ) { }

        var checkpoint = Assert.Single(
            logger.Entries,
            entry => Equals(entry.GetProperty("CheckpointName"), "Review Ready")
        );
        Assert.Equal("checkpoint", checkpoint.GetProperty("CheckpointNodeId"));
        Assert.False(string.IsNullOrWhiteSpace(checkpoint.GetProperty("CheckpointId")?.ToString()));
    }

    [Fact]
    public async Task ExecuteStreamingAsync_CheckpointAfterToolApproval_EmitsOnce()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "checkpoint-approval-flow" };
        var agentId = Guid.CreateVersion7();
        var worker = new ApprovalRequestAgent();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Worker",
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "checkpoint",
                Kind = AgentflowNodeKind.CheckpointMarker,
                ConfigJson = """{"checkpointName":"Ready"}""",
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "agent-checkpoint",
                SourceNodeId = "agent",
                TargetNodeId = "checkpoint",
            },
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "checkpoint-output",
                SourceNodeId = "checkpoint",
                TargetNodeId = "output",
            },
        };
        var logger = new CapturingLogger<AgentflowRuntimeService>();
        var service = new AgentflowRuntimeService(
            logger,
            new TestRepository<Agentflow>([agentflow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            new AgentflowDomainService(TimeProvider.System),
            new StubAgentRuntimeService(_ => worker),
            new StubProviderSessionState(),
            new RecordingSummaryService()
        );

        await foreach (
            var _ in service.ExecuteStreamingAsync(
                agentflow.Id,
                "run",
                TestContext.Current.CancellationToken,
                taskId: Guid.CreateVersion7(),
                humanGateApprovalHandler: new DelayedApprovalHandler("once")
            )
        ) { }

        Assert.Single(logger.Entries, entry => Equals(entry.GetProperty("CheckpointName"), "Ready"));
    }

    [Fact]
    public async Task AgentflowRuntime_ExecuteStreamingAsync_ForwardsSessionEnvironmentVariables()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "environment-flow" };
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Worker",
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "agent-output",
                SourceNodeId = "agent",
                TargetNodeId = "output",
            },
        };
        var agentRuntimeService = new StubAgentRuntimeService(agentId);
        var runtimeService = new AgentflowRuntimeService(
            NullLogger<AgentflowRuntimeService>.Instance,
            new TestRepository<Agentflow>([agentflow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            new AgentflowDomainService(TimeProvider.System),
            agentRuntimeService,
            new StubProviderSessionState(),
            new RecordingSummaryService()
        );
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var task = new TaskProjection
        {
            ProjectId = projectId,
            ProjectConversationId = conversationId,
            ContextId = "environment-context",
            TaskId = Guid.CreateVersion7(),
        };
        var settings = new SettingCommand(
            projectId,
            new Dictionary<string, string> { ["SESSION_ONLY"] = "session" },
            task.ContextId
        );
        var runtime = new AgentflowRuntime(agentflow.Id, task, settings, runtimeService);
        var command = new ExecCommand(
            AgentRuntimeType.Agentflow,
            new AgwUserInput { Contents = [new AgwTextContent { Content = "run" }] }
        );

        await foreach (
            var _ in runtime.ExecuteStreamingAsync(
                command,
                new DelayedApprovalHandler(),
                TestContext.Current.CancellationToken
            )
        )
        {
            break;
        }

        Assert.NotNull(agentRuntimeService.LastEnvironmentVariables);
        Assert.Equal("session", agentRuntimeService.LastEnvironmentVariables["SESSION_ONLY"]);
        Assert.Equal(conversationId, agentRuntimeService.LastConversationId);
        Assert.All(agentRuntimeService.CreatedAgents, agent => Assert.True(agent.Disposed));
    }

    [Fact]
    public async Task GetMermaidAsync_NestedWorkflow_DisposesNestedAgents()
    {
        var innerFlow = new Agentflow { Id = Guid.CreateVersion7(), Name = "inner" };
        var outerFlow = new Agentflow { Id = Guid.CreateVersion7(), Name = "outer" };
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = innerFlow.Id,
                NodeId = "inner-agent",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = innerFlow.Id,
                NodeId = "inner-output",
                Kind = AgentflowNodeKind.Output,
            },
            new AgentflowNode
            {
                AgentflowId = outerFlow.Id,
                NodeId = "nested",
                Kind = AgentflowNodeKind.WorkflowAsAgent,
                RelateId = innerFlow.Id,
            },
            new AgentflowNode
            {
                AgentflowId = outerFlow.Id,
                NodeId = "outer-output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = innerFlow.Id,
                EdgeId = "inner-output",
                SourceNodeId = "inner-agent",
                TargetNodeId = "inner-output",
            },
            new AgentflowEdge
            {
                AgentflowId = outerFlow.Id,
                EdgeId = "outer-output",
                SourceNodeId = "nested",
                TargetNodeId = "outer-output",
            },
        };
        var agentRuntimeService = new StubAgentRuntimeService(agentId);
        var service = new AgentflowRuntimeService(
            NullLogger<AgentflowRuntimeService>.Instance,
            new TestRepository<Agentflow>([innerFlow, outerFlow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            new AgentflowDomainService(TimeProvider.System),
            agentRuntimeService,
            new StubProviderSessionState(),
            new RecordingSummaryService()
        );

        var mermaid = await service.GetMermaidAsync(outerFlow.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(mermaid);
        Assert.All(agentRuntimeService.CreatedAgents, agent => Assert.True(agent.Disposed));
    }

    [Fact]
    public async Task ExecuteStreamingAsync_ApprovalCancellation_DisposesWorkflowAgents()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "cancelled" };
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "human",
                Kind = AgentflowNodeKind.HumanGate,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "human-agent",
                SourceNodeId = "human",
                TargetNodeId = "agent",
            },
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "agent-output",
                SourceNodeId = "agent",
                TargetNodeId = "output",
            },
        };
        var agentRuntimeService = new StubAgentRuntimeService(agentId);
        var service = new AgentflowRuntimeService(
            NullLogger<AgentflowRuntimeService>.Instance,
            new TestRepository<Agentflow>([agentflow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            new AgentflowDomainService(TimeProvider.System),
            agentRuntimeService,
            new StubProviderSessionState(),
            new RecordingSummaryService()
        );
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await foreach (
            var _ in service.ExecuteStreamingAsync(
                agentflow.Id,
                "cancel",
                cancellationSource.Token,
                humanGateApprovalHandler: new CancellingApprovalHandler()
            )
        ) { }

        Assert.All(agentRuntimeService.CreatedAgents, agent => Assert.True(agent.Disposed));
    }

    [Fact]
    public async Task ExecuteStreamingAsync_HumanGateApproved_PersistsWaitDurationAndInput()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "approval-flow" };
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "human",
                Kind = AgentflowNodeKind.HumanGate,
                Name = "Approval",
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Worker",
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "human-agent",
                SourceNodeId = "human",
                TargetNodeId = "agent",
            },
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "agent-output",
                SourceNodeId = "agent",
                TargetNodeId = "output",
            },
        };
        var traceStore = new CollectingTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            traceStore,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance
        );
        await collector.StartAsync(TestContext.Current.CancellationToken);
        var service = new AgentflowRuntimeService(
            NullLogger<AgentflowRuntimeService>.Instance,
            new TestRepository<Agentflow>([agentflow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            new AgentflowDomainService(TimeProvider.System),
            new StubAgentRuntimeService(agentId),
            new StubProviderSessionState(),
            new RecordingSummaryService()
        );
        var projectId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();

        await foreach (
            var _ in service.ExecuteStreamingAsync(
                agentflow.Id,
                "review this",
                TestContext.Current.CancellationToken,
                projectId,
                "context-approval",
                taskId,
                new DelayedApprovalHandler()
            )
        ) { }

        var trace = await traceStore.WaitForAsync(
            item => item.NodeKind == AgentflowNodeKind.HumanGate,
            TestContext.Current.CancellationToken
        );
        Assert.Equal("human", trace.NodeId);
        Assert.Equal("Approval", trace.NodeName);
        Assert.Equal(projectId, trace.ProjectId);
        Assert.Equal("context-approval", trace.ContextId);
        Assert.Equal(taskId, trace.TaskId);
        Assert.Null(trace.AgentId);
        Assert.Null(trace.AgentName);
        Assert.Contains("review this", trace.Input, StringComparison.Ordinal);
        Assert.Equal(AgentflowNodeExecutionStatus.Succeeded, trace.Status);
        Assert.True(trace.DurationMilliseconds >= 10);

        await collector.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_SummaryEnabledOutput_EmitsOneResult()
    {
        var agentId = Guid.CreateVersion7();
        var modelProviderId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var agentflow = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "summary-flow",
            SummaryModelProviderId = modelProviderId,
        };
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
                Instructions = "Keep it short.",
                ConfigJson = """{"enableSummary":true}""",
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "agent-output",
                SourceNodeId = "agent",
                TargetNodeId = "output",
            },
        };
        var summaryService = new RecordingSummaryService();
        var service = new AgentflowRuntimeService(
            NullLogger<AgentflowRuntimeService>.Instance,
            new TestRepository<Agentflow>([agentflow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            new AgentflowDomainService(TimeProvider.System),
            new StubAgentRuntimeService(agentId),
            new StubProviderSessionState(),
            summaryService
        );
        var messages = new List<AgwMessage>();

        await foreach (
            var message in service.ExecuteStreamingAsync(
                agentflow.Id,
                "workflow input",
                TestContext.Current.CancellationToken,
                projectId,
                "context-1",
                Guid.CreateVersion7()
            )
        )
        {
            messages.Add(message);
        }

        Assert.Single(
            messages,
            message =>
                message.AdditionalProperties?.TryGetValue("type", out var type) == true
                && string.Equals(type?.ToString(), "result", StringComparison.Ordinal)
        );
        var call = Assert.Single(summaryService.Calls);
        Assert.Equal(modelProviderId, call.ModelProviderId);
        Assert.Equal(projectId, call.ProjectId);
        Assert.Equal("context-1", call.ContextId);
        Assert.Equal("Keep it short.", call.CustomInstructions);
        Assert.Equal(["done"], call.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ExecuteStreamingAsync_TodoToolBlock_WithoutToolInvocation_DoesNotPersistStateSnapshot()
    {
        var agentId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "todo-flow" };
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = agentflow.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = agentflow.Id,
                EdgeId = "agent-output",
                SourceNodeId = "agent",
                TargetNodeId = "output",
            },
        };
        var historyWriter = new RecordingConversationHistoryWriter();
        var service = new AgentflowRuntimeService(
            NullLogger<AgentflowRuntimeService>.Instance,
            new TestRepository<Agentflow>([agentflow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            new AgentflowDomainService(TimeProvider.System),
            new StubAgentRuntimeService(_ => new TrackingAIAgent(enableTodo: true)),
            new StubProviderSessionState(),
            new RecordingSummaryService(),
            conversationHistoryWriter: historyWriter
        );
        var messages = new List<AgwMessage>();

        await foreach (
            var message in service.ExecuteStreamingAsync(
                agentflow.Id,
                "workflow input",
                TestContext.Current.CancellationToken,
                projectId,
                "context-1",
                Guid.CreateVersion7()
            )
        )
        {
            messages.Add(message);
        }

        Assert.DoesNotContain(
            messages,
            message => IsMessageType(message.AdditionalProperties, ToolMessageTypes.TodoSnapshot)
        );
        Assert.Empty(historyWriter.Calls);
    }

    [Fact]
    public void CreateWorkflowOutputMessages_ListOfChatMessages_ReturnsAgwMessages()
    {
        var output = new List<ChatMessage> { new(ChatRole.Assistant, "Bonjour") { AuthorName = "french-translator" } };

        var messages = AgentflowRuntimeService.CreateWorkflowOutputMessages(output);

        var message = Assert.Single(messages);
        Assert.Equal("french-translator", message.Author);
        var content = Assert.IsType<AgwTextContent>(Assert.Single(message.Contents));
        Assert.Equal("Bonjour", content.Content);
    }

    [Fact]
    public void CreateWorkflowInputMessages_SetsDefaultUserAuthor()
    {
        var input = "Translate Hello World";

        var messages = AgentflowRuntimeService.CreateWorkflowInputMessages(input);

        var message = Assert.Single(messages);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal(Constants.DefaultInputAuthor, message.AuthorName);
        Assert.Equal(input, message.Text);
    }

    [Fact]
    public void CreateWorkflowInputMessages_Handoff_PrependsContextAndStampsTargetCursor()
    {
        var agentflowId = Guid.CreateVersion7();
        var handoffMessage = new ChatMessage(ChatRole.Assistant, "approved plan");
        ConversationHandoffMetadata.MarkHandoffMessage(handoffMessage);
        var input = new AgwUserInput
        {
            MessageId = "current-input",
            Contents = [new AgwTextContent { Content = "start implementation" }],
        };

        var messages = AgentflowRuntimeService.CreateWorkflowInputMessages(
            input,
            agentflowId,
            new ConversationHandoff([handoffMessage], 29)
        );

        Assert.Equal(["approved plan", "start implementation"], messages.Select(message => message.Text));
        var current = messages[1];
        Assert.Equal("current-input", current.MessageId);
        Assert.Equal(29L, current.AdditionalProperties![ConversationHandoffMetadata.ThroughSequenceKey]);
        var text = Assert.IsType<TextContent>(Assert.Single(current.Contents));
        Assert.Equal("agentflow", text.AdditionalProperties!["targetType"]);
        Assert.Equal(agentflowId.ToString("D"), text.AdditionalProperties["targetId"]);
    }

    private sealed class DelayedApprovalHandler : IHumanGateApprovalHandler
    {
        private readonly string _approvalScope;

        public DelayedApprovalHandler(string approvalScope = "once")
        {
            _approvalScope = approvalScope;
        }

        public async ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
            HumanGateApprovalRequest request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(20, cancellationToken);
            return new HumanGateApprovalDecision(request.RequestId, true, "approved", _approvalScope);
        }
    }

    private sealed class CancellingApprovalHandler : IHumanGateApprovalHandler
    {
        public async ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
            HumanGateApprovalRequest request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HumanGateApprovalDecision(request.RequestId, true, null);
        }
    }

    private sealed class CollectingTraceStore : IAgentflowNodeExecutionTraceStore
    {
        private readonly object _lock = new();
        private readonly List<AgentflowTrace> _traces = [];
        private readonly TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SaveAsync(AgentflowTrace trace, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _traces.Add(trace);
            }

            _changed.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task<AgentflowTrace> WaitForAsync(
            Func<AgentflowTrace, bool> predicate,
            CancellationToken cancellationToken
        )
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            while (true)
            {
                lock (_lock)
                {
                    var trace = _traces.FirstOrDefault(predicate);
                    if (trace != null)
                    {
                        return trace;
                    }
                }

                await _changed.Task.WaitAsync(timeout.Token);
            }
        }
    }

    private sealed class TestRepository<TEntity> : IRepository<TEntity>
        where TEntity : class
    {
        private readonly List<TEntity> _items;
        private readonly Func<TEntity, object> _getId;

        public TestRepository(IEnumerable<TEntity> items, Func<TEntity, object> getId)
        {
            _items = items.ToList();
            _getId = getId;
        }

        public IQueryable<TEntity> Queryable => _items.AsQueryable();

        public Task<TEntity?> GetByIdAsync(object id) =>
            Task.FromResult(_items.FirstOrDefault(item => Equals(_getId(item), id)));

        public Task<TEntity?> SingleOrDefaultAsync(
            System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(_items.AsQueryable().SingleOrDefault(predicate));

        public Task<IReadOnlyList<TEntity>> ListAsync(
            System.Linq.Expressions.Expression<Func<TEntity, bool>>? predicate = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null
        )
        {
            IQueryable<TEntity> query = _items.AsQueryable();
            if (predicate != null)
            {
                query = query.Where(predicate);
            }

            if (orderBy != null)
            {
                query = orderBy(query);
            }

            return Task.FromResult<IReadOnlyList<TEntity>>(query.ToList());
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(
            System.Linq.Expressions.Expression<Func<TEntity, bool>>? predicate,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy,
            params System.Linq.Expressions.Expression<Func<TEntity, object>>[] includes
        ) => ListAsync(predicate, orderBy);

        public Task AddAsync(TEntity entity)
        {
            _items.Add(entity);
            return Task.CompletedTask;
        }

        public void Update(TEntity entity) { }

        public void Remove(TEntity entity) => _items.Remove(entity);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubAgentRuntimeService : IAgentRuntimeService
    {
        private readonly Guid _agentId;
        private readonly Func<Guid, AIAgent?>? _agentFactory;

        public IReadOnlyDictionary<string, string>? LastEnvironmentVariables { get; private set; }

        public Guid LastConversationId { get; private set; }

        public List<TrackingAIAgent> CreatedAgents { get; } = [];

        public StubAgentRuntimeService(Guid agentId)
        {
            _agentId = agentId;
        }

        public StubAgentRuntimeService(Func<Guid, AIAgent?> agentFactory)
        {
            _agentFactory = agentFactory;
        }

        public Task<AIAgent?> CreateAiAgentAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            CreateAiAgentAsync(agentId, null, false, cancellationToken);

        public Task<AIAgent?> CreateAiAgentAsync(
            Guid agentId,
            Guid? projectId,
            bool resume,
            CancellationToken cancellationToken = default
        )
        {
            return CreateAiAgentAsync(agentId, projectId, resume, environmentVariables: null, cancellationToken);
        }

        public Task<AIAgent?> CreateAiAgentAsync(
            Guid agentId,
            Guid? projectId,
            bool resume,
            IReadOnlyDictionary<string, string>? environmentVariables,
            CancellationToken cancellationToken = default
        )
        {
            LastEnvironmentVariables = environmentVariables;
            AIAgent? agent = _agentFactory?.Invoke(agentId) ?? (agentId == _agentId ? new TrackingAIAgent() : null);
            if (agent is TrackingAIAgent trackingAgent)
            {
                CreatedAgents.Add(trackingAgent);
            }

            return Task.FromResult(agent);
        }

        public Task<AIAgent?> CreateAgentflowNodeAgentAsync(
            Guid agentId,
            Guid? projectId,
            Guid conversationId,
            IReadOnlyDictionary<string, string>? environmentVariables,
            CancellationToken cancellationToken = default
        )
        {
            LastConversationId = conversationId;
            return CreateAiAgentAsync(agentId, projectId, resume: false, environmentVariables, cancellationToken);
        }

        public Task<AgentRuntime?> CreateRuntimeAsync(
            Guid agentId,
            TaskProjection task,
            SettingCommand settings,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            AgentRuntime session,
            AgwUserInput input,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
            AgentRuntime session,
            AgwUserInput input,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<AgentExecutionResult?> ExecuteByIdAsync(
            AgentExecuteByIdRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();
    }

    private sealed class TrackingAIAgent : DelegatingAIAgent, IAsyncDisposable
    {
        private readonly TodoProvider? _todoProvider;

        public TrackingAIAgent(bool enableTodo = false)
            : base(
                new ChatClientAgent(
                    new StubChatClient(),
                    new ChatClientAgentOptions { Id = "tracking", Name = "tracking" }
                )
            )
        {
            _todoProvider = enableTodo ? new TodoProvider() : null;
        }

        public bool Disposed { get; private set; }

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            base.GetService(serviceType, serviceKey) ?? _todoProvider?.GetService(serviceType, serviceKey);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }
    }

    private sealed class ApprovalRequestAgent : AIAgent
    {
        protected override string? IdCore => "approval-agent";

        public override string? Name => "Approval Agent";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ApprovalRequestSession());

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement sessionState,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new ApprovalRequestSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            var response = messages
                .SelectMany(message => message.Contents)
                .FirstOrDefault(content =>
                    content is ToolApprovalResponseContent or AlwaysApproveToolApprovalResponseContent
                );
            if (response != null)
            {
                var approvalScope = response switch
                {
                    AlwaysApproveToolApprovalResponseContent { AlwaysApproveTool: true } => "always-tool",
                    AlwaysApproveToolApprovalResponseContent { AlwaysApproveToolWithArguments: true } =>
                        "always-arguments",
                    _ => "once",
                };
                yield return new AgentResponseUpdate(ChatRole.Assistant, approvalScope);
                yield break;
            }

            yield return new AgentResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new ToolApprovalRequestContent(
                        "approval-1",
                        new FunctionCallContent("call-1", "run_shell", new Dictionary<string, object?>())
                    ),
                ],
            };
        }

        private sealed class ApprovalRequestSession : AgentSession;
    }

    private sealed class StubProviderSessionState : IProviderSessionState
    {
        public void InitializeSessionState(AgentSession session, string contextId, Guid projectId) { }

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope
        ) { }

        public bool TryGetProjectContext(AgentSession session, out Guid projectId, out string contextId)
        {
            projectId = default;
            contextId = string.Empty;
            return false;
        }
    }

    private sealed class RecordingConversationHistoryWriter : IConversationHistoryWriter
    {
        public List<HistoryCall> Calls { get; } = [];

        public Task AppendAsync(
            Guid projectId,
            string contextId,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add(new HistoryCall(projectId, contextId, messages.ToList()));
            return Task.CompletedTask;
        }
    }

    private sealed record HistoryCall(Guid ProjectId, string ContextId, IReadOnlyList<ChatMessage> Messages);

    private static bool IsMessageType(AdditionalPropertiesDictionary? properties, string expectedType) =>
        properties?.TryGetValue("type", out var type) == true
        && string.Equals(type?.ToString(), expectedType, StringComparison.Ordinal);

    private sealed class RecordingSummaryService : IAgentTurnSummaryService
    {
        public List<SummaryCall> Calls { get; } = [];

        public Task<ChatMessage> CreateResultAsync(
            Guid modelProviderId,
            IReadOnlyList<ChatMessage> sourceMessages,
            Guid projectId,
            string contextId,
            string? customInstructions,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add(new SummaryCall(modelProviderId, sourceMessages, projectId, contextId, customInstructions));
            return Task.FromResult(AgentTurnSummaryService.CreateResultMessage("summary"));
        }
    }

    private sealed record SummaryCall(
        Guid ModelProviderId,
        IReadOnlyList<ChatMessage> Messages,
        Guid ProjectId,
        string ContextId,
        string? CustomInstructions
    );

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            Entries.Add(new LogEntry(properties.ToList()));
        }
    }

    private sealed record LogEntry(IReadOnlyList<KeyValuePair<string, object?>> Properties)
    {
        public object? GetProperty(string name) =>
            Properties.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.Ordinal)).Value;
    }
}
