using System.Collections.Concurrent;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public partial class AgentflowTurnExecutorTests
{
    [Fact]
    public async Task RunAsync_LinearAgentflow_WritesRunningAndCompletedForEveryNode()
    {
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.PromptAdapter, AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => new ScriptedAgent(["answer"])
        );

        var messages = await CollectAsync(
            fixture.Service.ExecuteStreamingAsync(fixture.Flow.Id, "input", TestContext.Current.CancellationToken)
        );
        var statuses = messages
            .Where(message => IsMessageType(message.AdditionalProperties, AgwMessageTypes.AgentflowNodeStatus))
            .ToList();

        foreach (var nodeId in new[] { "input", "node-0", "node-1", "node-2" })
        {
            var nodeStatuses = statuses
                .Where(message => Equals(message.AdditionalProperties!["nodeId"], nodeId))
                .ToList();
            Assert.Contains(
                nodeStatuses,
                message => Equals(message.AdditionalProperties!["status"], AgwAgentflowNodeStatus.Running)
            );
            Assert.Contains(
                nodeStatuses,
                message => Equals(message.AdditionalProperties!["status"], AgwAgentflowNodeStatus.Completed)
            );
            Assert.Equal(AgwAgentflowNodeStatus.Completed, nodeStatuses[^1].AdditionalProperties!["status"]);
        }

        var expectedNodeIds = new[] { "input", "node-0", "node-1", "node-2" };
        Assert.All(
            statuses,
            message => Assert.Contains(message.AdditionalProperties!["nodeId"]?.ToString(), expectedNodeIds)
        );
    }

    [Fact]
    public async Task RunAsync_HumanGateAnswered_WritesWaitingThenCompletedWithInteractionId()
    {
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.HumanGate, AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => new ScriptedAgent(["answer"])
        );
        var sink = new InteractionTestSink();
        var interactionPublished = new TaskCompletionSource<AgwMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var order = new ConcurrentQueue<string>();
        sink.OnWrite = (message, _) =>
        {
            order.Enqueue("interaction-request");
            interactionPublished.TrySetResult(message);
            return ValueTask.CompletedTask;
        };
        var interactions = new InProcessInteractionSession(sink);
        var messages = new ConcurrentQueue<AgwMessage>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var execution = Task.Run(async () =>
        {
            await foreach (
                var message in fixture.Service.ExecuteStreamingAsync(
                    fixture.Flow.Id,
                    "input",
                    cancellation.Token,
                    interactionHandler: interactions
                )
            )
            {
                messages.Enqueue(message);
                if (
                    IsMessageType(message.AdditionalProperties, AgwMessageTypes.AgentflowNodeStatus)
                    && Equals(message.AdditionalProperties!["status"], AgwAgentflowNodeStatus.Waiting)
                )
                    order.Enqueue("waiting");
            }
        });

        try
        {
            var requestMessage = await interactionPublished.Task.WaitAsync(cancellation.Token);
            Assert.Equal(["waiting", "interaction-request"], order.Take(2));
            var request = InteractionTestData.Read(requestMessage);
            Assert.True(
                await interactions.TrySubmitAsync(
                    InteractionTestData.Decision(request, approved: true, choiceId: "choice-1"),
                    TestContext.Current.CancellationToken
                )
            );
            await execution.WaitAsync(cancellation.Token);

            var gateStatuses = messages
                .Where(message =>
                    IsMessageType(message.AdditionalProperties, AgwMessageTypes.AgentflowNodeStatus)
                    && Equals(message.AdditionalProperties!["nodeId"], "node-0")
                )
                .ToList();
            var waiting = gateStatuses.Single(message =>
                Equals(message.AdditionalProperties!["status"], AgwAgentflowNodeStatus.Waiting)
            );
            var completed = gateStatuses.Single(message =>
                Equals(message.AdditionalProperties!["status"], AgwAgentflowNodeStatus.Completed)
                && message.AdditionalProperties!.ContainsKey("interactionId")
            );
            Assert.Equal(request.InteractionId, waiting.AdditionalProperties!["interactionId"]);
            Assert.Equal(request.InteractionId, completed.AdditionalProperties!["interactionId"]);
        }
        finally
        {
            cancellation.Cancel();
            interactions.CancelAll();
            await execution;
        }
    }

    [Fact]
    public async Task RunAsync_ToolApprovalAnswered_WritesWaitingThenRunningWithInteractionId()
    {
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => new ApprovalRequestAgent()
        );
        var sink = new InteractionTestSink();
        var interactionPublished = new TaskCompletionSource<AgwMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        sink.OnWrite = (message, _) =>
        {
            interactionPublished.TrySetResult(message);
            return ValueTask.CompletedTask;
        };
        var interactions = new InProcessInteractionSession(sink);
        var messages = new ConcurrentQueue<AgwMessage>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var execution = Task.Run(async () =>
        {
            await foreach (
                var message in fixture.Service.ExecuteStreamingAsync(
                    fixture.Flow.Id,
                    "input",
                    cancellation.Token,
                    interactionHandler: interactions
                )
            )
                messages.Enqueue(message);
        });

        try
        {
            var requestMessage = await interactionPublished.Task.WaitAsync(cancellation.Token);
            var request = InteractionTestData.Read(requestMessage);
            Assert.Equal("node-0", request.Source.NodeId);
            Assert.Equal("node-0", request.Source.NodePath);
            Assert.Equal(
                InteractionIdentity.ForProviderRequest("node-0", request.Source.ProviderRequestId!),
                request.InteractionId
            );
            Assert.True(
                await interactions.TrySubmitAsync(
                    InteractionTestData.Decision(request, approved: true),
                    TestContext.Current.CancellationToken
                )
            );
            await execution.WaitAsync(cancellation.Token);

            var agentStatuses = messages
                .Where(message =>
                    IsMessageType(message.AdditionalProperties, AgwMessageTypes.AgentflowNodeStatus)
                    && Equals(message.AdditionalProperties!["nodeId"], "node-0")
                    && message.AdditionalProperties.ContainsKey("interactionId")
                    && Equals(message.AdditionalProperties["interactionId"], request.InteractionId)
                )
                .ToList();
            Assert.Equal(
                [AgwAgentflowNodeStatus.Waiting, AgwAgentflowNodeStatus.Running],
                agentStatuses.Select(message => message.AdditionalProperties!["status"]?.ToString())
            );
        }
        finally
        {
            cancellation.Cancel();
            interactions.CancelAll();
            await execution;
        }
    }

    [Fact]
    public async Task RunAsync_NodeFails_WritesFailedBeforeWorkflowError()
    {
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => new ScriptedAgent([], fail: true)
        );

        var messages = await CollectAsync(
            fixture.Service.ExecuteStreamingAsync(fixture.Flow.Id, "input", TestContext.Current.CancellationToken)
        );

        var failedIndex = messages.FindIndex(message =>
            IsMessageType(message.AdditionalProperties, AgwMessageTypes.AgentflowNodeStatus)
            && Equals(message.AdditionalProperties!["nodeId"], "node-0")
            && Equals(message.AdditionalProperties["status"], AgwAgentflowNodeStatus.Failed)
        );
        var errorIndex = messages.FindIndex(message =>
            IsMessageType(message.AdditionalProperties, AgwMessageTypes.WorkflowError)
        );

        Assert.True(failedIndex >= 0);
        Assert.True(errorIndex > failedIndex);
    }

    [Fact]
    public async Task RunAsync_AgentflowNodes_TraceExtraRecordsAgentflowRevision()
    {
        var flow = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "revision-two",
            CreateBy = "tester",
        };
        var agentId = Guid.CreateVersion7();
        var memberAgentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = flow.Id,
                AgentflowRevision = 2,
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
            },
            new AgentflowNode
            {
                AgentflowId = flow.Id,
                AgentflowRevision = 2,
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = flow.Id,
                AgentflowRevision = 2,
                NodeId = "human",
                Kind = AgentflowNodeKind.HumanGate,
                RoutingStrategy = AgentflowRoutingStrategy.Choice,
                ConfigJson = GateOptions(),
            },
            new AgentflowNode
            {
                AgentflowId = flow.Id,
                AgentflowRevision = 2,
                NodeId = "block",
                Kind = AgentflowNodeKind.GroupChatBlock,
                ConfigJson = AgentflowDefinitionJson.WriteOptions(new GroupChatBlockOptions(1)),
            },
            new AgentflowNode
            {
                AgentflowId = flow.Id,
                AgentflowRevision = 2,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = flow.Id,
                AgentflowRevision = 2,
                EdgeId = "input-agent",
                SourceNodeId = "input",
                TargetNodeId = "agent",
            },
            new AgentflowEdge
            {
                AgentflowId = flow.Id,
                AgentflowRevision = 2,
                EdgeId = "agent-human",
                SourceNodeId = "agent",
                TargetNodeId = "human",
            },
            new AgentflowEdge
            {
                AgentflowId = flow.Id,
                AgentflowRevision = 2,
                EdgeId = "human-block",
                SourceNodeId = "human",
                TargetNodeId = "block",
                ChoiceId = "choice-1",
            },
            new AgentflowEdge
            {
                AgentflowId = flow.Id,
                AgentflowRevision = 2,
                EdgeId = "block-output",
                SourceNodeId = "block",
                TargetNodeId = "output",
            },
        };
        var members = new[]
        {
            new AgentflowBlockMember
            {
                AgentflowId = flow.Id,
                AgentflowRevision = 2,
                NodeId = "block",
                MemberId = "m1",
                Kind = AgentflowNodeKind.Agent,
                RelateId = memberAgentId,
                Name = "Member",
                SortOrder = 0,
            },
        };
        var traceStore = new CollectingTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            traceStore,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance
        );
        var interactionSink = new InteractionTestSink();
        var interactions = new InProcessInteractionSession(interactionSink);
        var responseAccepted = false;
        interactionSink.OnWrite = async (message, token) =>
        {
            var request = InteractionTestData.Read(message);
            responseAccepted = await interactions.TrySubmitAsync(
                InteractionTestData.Decision(request, approved: true, choiceId: "choice-1"),
                token
            );
        };
        await collector.StartAsync(TestContext.Current.CancellationToken);
        var host = CreateTestHost(
            NullLogger<AgentflowCheckpointSupport>.Instance,
            new TestRepository<Agentflow>([flow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, node => (node.AgentflowId, node.NodeId)),
            new TestRepository<AgentflowEdge>(edges, edge => (edge.AgentflowId, edge.EdgeId)),
            new StubAgentRuntimeFactory(id => new ScriptedAgent([id == agentId ? "agent-output" : "member-output"])),
            new StubProviderSessionState(),
            new RecordingSummaryService(),
            memberRepository: new TestRepository<AgentflowBlockMember>(
                members,
                member => (member.AgentflowId, member.NodeId, member.MemberId)
            )
        );

        var messages = await CollectAsync(
            host.ExecuteStreamingAsync(
                flow.Id,
                "input",
                TestContext.Current.CancellationToken,
                interactionHandler: interactions,
                snapshot: new AgentflowRevisionSnapshot(2, [])
            )
        );
        Assert.True(responseAccepted);

        var agentTrace = await traceStore.WaitForAsync(
            trace => trace.AgentflowId == flow.Id && trace.NodeId == "agent",
            TestContext.Current.CancellationToken
        );
        var memberTrace = await traceStore.WaitForAsync(
            trace => trace.AgentflowId == flow.Id && trace.NodeId == "block",
            TestContext.Current.CancellationToken
        );
        var gateTrace = await traceStore.WaitForAsync(
            trace => trace.AgentflowId == flow.Id && trace.NodeId == "human",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, agentTrace.Extra.AgentflowRevision);
        Assert.Equal(2, memberTrace.Extra.AgentflowRevision);
        Assert.Equal(2, gateTrace.Extra.AgentflowRevision);
        AssertNodeAttribution(messages, "agent-output", "agent", "agent");
        AssertNodeAttribution(messages, "member-output", "block", "block/block.m1", "block");
        var gateRequest = InteractionTestData.Read(Assert.Single(interactionSink.Messages));
        Assert.Equal("human", gateRequest.Source.NodeId);
        Assert.Equal("human", gateRequest.Source.NodePath);
        await collector.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_NestedAgentflow_TraceRecordsPinnedDependencyRevision()
    {
        var root = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "root",
            CreateBy = "tester",
        };
        var nested = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "nested",
            CreateBy = "tester",
        };
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = root.Id,
                AgentflowRevision = 2,
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
            },
            new AgentflowNode
            {
                AgentflowId = root.Id,
                AgentflowRevision = 2,
                NodeId = "nested",
                Kind = AgentflowNodeKind.WorkflowAsAgent,
                RelateId = nested.Id,
            },
            new AgentflowNode
            {
                AgentflowId = root.Id,
                AgentflowRevision = 2,
                NodeId = "root-output",
                Kind = AgentflowNodeKind.Output,
            },
            new AgentflowNode
            {
                AgentflowId = nested.Id,
                AgentflowRevision = 3,
                NodeId = "inner-agent",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = nested.Id,
                AgentflowRevision = 3,
                NodeId = "inner-output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = root.Id,
                AgentflowRevision = 2,
                EdgeId = "input-nested",
                SourceNodeId = "input",
                TargetNodeId = "nested",
            },
            new AgentflowEdge
            {
                AgentflowId = root.Id,
                AgentflowRevision = 2,
                EdgeId = "nested-output",
                SourceNodeId = "nested",
                TargetNodeId = "root-output",
            },
            new AgentflowEdge
            {
                AgentflowId = nested.Id,
                AgentflowRevision = 3,
                EdgeId = "inner-agent-output",
                SourceNodeId = "inner-agent",
                TargetNodeId = "inner-output",
            },
        };
        var traceStore = new CollectingTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            traceStore,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance
        );
        await collector.StartAsync(TestContext.Current.CancellationToken);
        var host = CreateTestHost(
            NullLogger<AgentflowCheckpointSupport>.Instance,
            new TestRepository<Agentflow>([root, nested], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, node => (node.AgentflowId, node.NodeId)),
            new TestRepository<AgentflowEdge>(edges, edge => (edge.AgentflowId, edge.EdgeId)),
            new StubAgentRuntimeFactory(_ => new ScriptedAgent(["answer"])),
            new StubProviderSessionState(),
            new RecordingSummaryService()
        );
        var snapshot = new AgentflowRevisionSnapshot(2, [new(root.Id, 2, "nested", null, nested.Id, 3)]);

        var messages = await CollectAsync(
            host.ExecuteStreamingAsync(root.Id, "input", TestContext.Current.CancellationToken, snapshot: snapshot)
        );

        var trace = await traceStore.WaitForAsync(
            item => item.AgentflowId == nested.Id && item.NodeId == "inner-agent",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(3, trace.Extra.AgentflowRevision);
        AssertNodeAttribution(messages, "answer", "nested", "nested/inner-agent");
        await collector.StopAsync(TestContext.Current.CancellationToken);
    }

    private static void AssertNodeAttribution(
        IReadOnlyList<AgwMessage> messages,
        string content,
        string nodeId,
        string nodePath,
        params string[] additionalExpectedNodePaths
    )
    {
        var attributedMessages = messages
            .Where(message =>
                message.Role == AiRole.Assistant
                && message.Contents.OfType<AgwTextContent>().Any(item => item.Content == content)
            )
            .ToList();

        Assert.NotEmpty(attributedMessages);
        var expectedNodePaths = additionalExpectedNodePaths.Append(nodePath).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(
            attributedMessages,
            message =>
                string.Equals(message.AdditionalProperties!["nodePath"]?.ToString(), nodePath, StringComparison.Ordinal)
        );
        Assert.All(
            attributedMessages,
            message =>
            {
                Assert.Equal(nodeId, message.AdditionalProperties!["nodeId"]);
                Assert.Contains(
                    expectedNodePaths,
                    expectedPath =>
                        string.Equals(
                            message.AdditionalProperties["nodePath"]?.ToString(),
                            expectedPath,
                            StringComparison.Ordinal
                        )
                );
            }
        );
    }
}
