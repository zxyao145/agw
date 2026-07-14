using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Summaries;
using Agw.Shared;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Tests;

[Collection(AgentflowExecutionTraceTestCollection.Name)]
public class AgentflowWorkflowCompilerTests
{
    private readonly AgentflowWorkflowCompiler _compiler = new();

    [Fact]
    public void BlockBuilders_AreDedicatedInternalStaticTypes()
    {
        var assembly = typeof(AgentflowWorkflowCompiler).Assembly;
        var builderTypeNames = new[]
        {
            "Agw.Agents.Execution.Agentflows.Builders.ConcurrentBlockBuilder",
            "Agw.Agents.Execution.Agentflows.Builders.GroupChatBlockBuilder",
            "Agw.Agents.Execution.Agentflows.Builders.HandoffBlockBuilder",
            "Agw.Agents.Execution.Agentflows.Builders.MagenticBlockBuilder",
        };

        foreach (var builderTypeName in builderTypeNames)
        {
            var builderType = assembly.GetType(builderTypeName);

            Assert.NotNull(builderType);
            Assert.True(builderType.IsAbstract);
            Assert.True(builderType.IsSealed);
            Assert.False(builderType.IsPublic);

            var buildMethod = builderType.GetMethod(
                "Build",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(buildMethod);
            Assert.Equal(typeof(ExecutorBinding), buildMethod.ReturnType);
        }
    }

    [Fact]
    public void ApplyInstructions_WithInstructions_UsesDefaultInputAuthor()
    {
        var result = AgentflowMessageTransforms.ApplyInstructions([], "Follow the workflow instructions.");

        var instruction = Assert.Single(result);
        Assert.Equal(Constants.DefaultInputAuthor, instruction.AuthorName);
    }

    [Fact]
    public async Task Compile_AgentNode_EmitsWorkflowTelemetryWithoutSensitiveData()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.Agents.AI.Workflows",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "observable-flow" };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "agent-node", Kind = AgentflowNodeKind.Agent, Name = "Node Alias" },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[] { Edge("agent-output", "agent-node", "output") };
        var loggingMiddleware = new ObservabilityMiddleware(NullLogger<ObservabilityMiddleware>.Instance);
        var agent = CreateAgent("agent-id", "persisted-agent")
            .AsBuilder()
            .Use(
                runFunc: loggingMiddleware.LogRunMiddleware,
                runStreamingFunc: loggingMiddleware.LogStreamingMiddleware)
            .Build();

        var workflow = _compiler.Compile(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { ["agent-node"] = agent });

        Assert.NotNull(workflow);
        await using (var run = await InProcessExecution.RunStreamingAsync(
                         workflow!,
                         new List<ChatMessage> { new(ChatRole.User, "sensitive input") },
                         cancellationToken: TestContext.Current.CancellationToken))
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken))
            {
            }
        }

        Assert.Contains(activities, activity => activity.OperationName == "workflow.build");
        Assert.Contains(activities, activity => activity.OperationName == "workflow.session");
        Assert.Contains(activities, activity => activity.OperationName == "workflow_invoke");
        Assert.Contains(activities, activity => activity.OperationName.StartsWith("executor.process", StringComparison.Ordinal));
        Assert.Contains(activities, activity => activity.OperationName == "edge_group.process");
        Assert.Contains(activities, activity => activity.OperationName == "message.send");
        Assert.DoesNotContain(activities.SelectMany(activity => activity.TagObjects), tag =>
            tag.Key is "executor.input" or "executor.output" or "message.content");
    }

    [Fact]
    public async Task Compile_AgentNode_PersistsNodeExecutionInputAndPersistentAgentName()
    {
        var store = new CapturingExecutionTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            store,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance);
        await collector.StartAsync(TestContext.Current.CancellationToken);

        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "persisted-flow" };
        var agentId = Guid.NewGuid();
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "agent-node",
                Kind = AgentflowNodeKind.Agent,
                Name = "Node Alias",
                RelateId = agentId,
                Instructions = "Node instruction",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var execution = new AgentflowExecutionTraceContext(Guid.NewGuid(), "context-1", Guid.NewGuid());
        var workflow = _compiler.Compile(
            agentflow,
            nodes,
            [Edge("agent-output", "agent-node", "output")],
            new Dictionary<string, AIAgent>
            {
                ["agent-node"] = CreateAgent("agent-id", "persisted-agent"),
            },
            sessionScope: null,
            execution);

        Assert.NotNull(workflow);
        await using (var run = await InProcessExecution.RunStreamingAsync(
                         workflow!,
                         new List<ChatMessage> { new(ChatRole.User, "workflow input") },
                         cancellationToken: TestContext.Current.CancellationToken))
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken))
            {
            }
        }

        var trace = await store.WaitForTraceAsync();
        Assert.Equal(agentflow.Id, trace.AgentflowId);
        Assert.Equal("agent-node", trace.NodeId);
        Assert.Equal("Node Alias", trace.NodeName);
        Assert.Equal(agentId, trace.AgentId);
        Assert.Equal("persisted-agent", trace.AgentName);
        Assert.Contains("workflow input", trace.Input, StringComparison.Ordinal);
        Assert.Contains("Node instruction", trace.Input, StringComparison.Ordinal);
        Assert.Equal(AgentflowNodeExecutionStatus.Succeeded, trace.Status);

        await collector.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Compile_DirectDagWithHumanAndCheckpoint_ReturnsWorkflow()
    {
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "review-flow" };
        var agentId = "agent-a";
        var nodes = new[]
        {
            new AgentflowNode { NodeId = agentId, Kind = AgentflowNodeKind.Agent, Name = "Reviewer" },
            new AgentflowNode
            {
                NodeId = "adapter",
                Kind = AgentflowNodeKind.PromptAdapter,
                Instructions = "Use the previous output as a review brief.",
            },
            new AgentflowNode { NodeId = "human", Kind = AgentflowNodeKind.HumanGate },
            new AgentflowNode { NodeId = "checkpoint", Kind = AgentflowNodeKind.CheckpointMarker },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("e1", agentId, "adapter"),
            Edge("e2", "adapter", "human"),
            Edge("e3", "human", "checkpoint"),
            Edge("e4", "checkpoint", "output"),
        };

        var workflow = _compiler.Compile(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { [agentId] = CreateAgent(agentId, "Reviewer") });

        Assert.NotNull(workflow);
        var mermaid = WorkflowVisualizer.ToMermaidString(workflow!);
        Assert.Contains("adapter", mermaid);
        Assert.Contains("human", mermaid);
        Assert.Contains("checkpoint", mermaid);
    }

    [Fact]
    public void Compile_FanOutAndFanInEdges_ReturnsWorkflow()
    {
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "parallel-flow" };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "start", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "left", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "right", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "join", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("fan-out-left", "start", "left", AgentflowEdgeKind.FanOut),
            Edge("fan-out-right", "start", "right", AgentflowEdgeKind.FanOut),
            Edge("fan-in-left", "left", "join", AgentflowEdgeKind.FanIn),
            Edge("fan-in-right", "right", "join", AgentflowEdgeKind.FanIn),
            Edge("to-output", "join", "output"),
        };

        var workflow = _compiler.Compile(agentflow, nodes, edges, new Dictionary<string, AIAgent>());

        Assert.NotNull(workflow);
        var mermaid = WorkflowVisualizer.ToMermaidString(workflow!);
        Assert.Contains("start", mermaid);
        Assert.Contains("join", mermaid);
    }

    [Theory]
    [InlineData(AgentflowNodeKind.ConcurrentBlock)]
    [InlineData(AgentflowNodeKind.GroupChatBlock)]
    [InlineData(AgentflowNodeKind.HandoffBlock)]
    [InlineData(AgentflowNodeKind.MagenticBlock)]
    public void Compile_BlockNodeWithParticipants_ReturnsWorkflow(AgentflowNodeKind blockKind)
    {
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "block-flow" };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "agent-a", Kind = AgentflowNodeKind.Agent, Name = "Agent A" },
            new AgentflowNode { NodeId = "agent-b", Kind = AgentflowNodeKind.Agent, Name = "Agent B" },
            new AgentflowNode
            {
                NodeId = "group",
                Kind = blockKind,
                Name = "GroupChat Room",
                ConfigJson = """{"participantNodeIds":["agent-a","agent-b"],"maxRounds":2}""",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("group-to-output", "group", "output"),
        };
        var agents = new Dictionary<string, AIAgent>
        {
            ["agent-a"] = CreateAgent("agent-a", "Agent A"),
            ["agent-b"] = CreateAgent("agent-b", "Agent B"),
        };

        var workflow = _compiler.Compile(agentflow, nodes, edges, agents);

        Assert.NotNull(workflow);
        Assert.Contains("group", WorkflowVisualizer.ToMermaidString(workflow!));
    }

    [Fact]
    public async Task Compile_ConcurrentBlock_PersistsOnlyParticipantAgents()
    {
        var store = new CollectingExecutionTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            store,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance);
        await collector.StartAsync(TestContext.Current.CancellationToken);
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "parallel-trace-flow" };
        var agentAId = Guid.NewGuid();
        var agentBId = Guid.NewGuid();
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "agent-a",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent A Node",
                RelateId = agentAId,
            },
            new AgentflowNode
            {
                NodeId = "agent-b",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent B Node",
                RelateId = agentBId,
            },
            new AgentflowNode
            {
                NodeId = "parallel",
                Kind = AgentflowNodeKind.ConcurrentBlock,
                Name = "Parallel Block",
                ConfigJson = """{"participantNodeIds":["agent-a","agent-b"]}""",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var workflow = _compiler.Compile(
            agentflow,
            nodes,
            [Edge("parallel-output", "parallel", "output")],
            new Dictionary<string, AIAgent>
            {
                ["agent-a"] = CreateAgent("agent-a", "persisted-agent-a"),
                ["agent-b"] = CreateAgent("agent-b", "persisted-agent-b"),
            },
            sessionScope: null,
            new AgentflowExecutionTraceContext(Guid.NewGuid(), "context-parallel", Guid.NewGuid()));

        Assert.NotNull(workflow);
        await using (var run = await InProcessExecution.RunStreamingAsync(
                         workflow!,
                         new List<ChatMessage> { new(ChatRole.User, "parallel input") },
                         cancellationToken: TestContext.Current.CancellationToken))
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken))
            {
            }
        }

        var traces = await store.WaitForCountAsync(2);
        Assert.Equal(2, traces.Count);
        Assert.Contains(traces, trace =>
            trace.NodeId == "agent-a" &&
            trace.AgentId == agentAId &&
            trace.AgentName == "persisted-agent-a");
        Assert.Contains(traces, trace =>
            trace.NodeId == "agent-b" &&
            trace.AgentId == agentBId &&
            trace.AgentName == "persisted-agent-b");
        Assert.DoesNotContain(traces, trace => trace.NodeId == "parallel");

        await collector.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Compile_BlockWithWorkflowAsAgentParticipant_PersistsOnlyNestedAgent()
    {
        var store = new CollectingExecutionTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            store,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance);
        await collector.StartAsync(TestContext.Current.CancellationToken);
        var execution = new AgentflowExecutionTraceContext(Guid.NewGuid(), "context-nested", Guid.NewGuid());
        var nestedAgentId = Guid.NewGuid();
        var nestedAgentflow = new Agentflow { Id = Guid.NewGuid(), Name = "nested-flow" };
        var nestedWorkflow = _compiler.Compile(
            nestedAgentflow,
            [
                new AgentflowNode
                {
                    NodeId = "nested-agent",
                    Kind = AgentflowNodeKind.Agent,
                    Name = "Nested Agent Node",
                    RelateId = nestedAgentId,
                },
                new AgentflowNode { NodeId = "nested-output", Kind = AgentflowNodeKind.Output },
            ],
            [Edge("nested-output", "nested-agent", "nested-output")],
            new Dictionary<string, AIAgent>
            {
                ["nested-agent"] = CreateAgent("nested-agent", "persisted-nested-agent"),
            },
            sessionScope: null,
            execution);
        Assert.NotNull(nestedWorkflow);

        var outerAgentflow = new Agentflow { Id = Guid.NewGuid(), Name = "outer-flow" };
        var outerWorkflow = _compiler.Compile(
            outerAgentflow,
            [
                new AgentflowNode
                {
                    NodeId = "nested-participant",
                    Kind = AgentflowNodeKind.WorkflowAsAgent,
                    Name = "Nested Workflow Node",
                    RelateId = nestedAgentflow.Id,
                },
                new AgentflowNode
                {
                    NodeId = "parallel",
                    Kind = AgentflowNodeKind.ConcurrentBlock,
                    Name = "Parallel Block",
                    ConfigJson = """{"participantNodeIds":["nested-participant"]}""",
                },
                new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
            ],
            [Edge("parallel-output", "parallel", "output")],
            new Dictionary<string, AIAgent>
            {
                ["nested-participant"] = nestedWorkflow!.AsAIAgent(),
            },
            sessionScope: null,
            execution);
        Assert.NotNull(outerWorkflow);

        await using (var run = await InProcessExecution.RunStreamingAsync(
                         outerWorkflow!,
                         new List<ChatMessage> { new(ChatRole.User, "nested input") },
                         cancellationToken: TestContext.Current.CancellationToken))
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken))
            {
            }
        }

        await collector.StopAsync(TestContext.Current.CancellationToken);
        var trace = Assert.Single(store.GetTraces());
        Assert.Equal(nestedAgentflow.Id, trace.AgentflowId);
        Assert.Equal("nested-agent", trace.NodeId);
        Assert.Equal(nestedAgentId, trace.AgentId);
        Assert.Equal("persisted-nested-agent", trace.AgentName);
    }

    [Fact]
    public async Task Compile_HumanGateBeforeAgent_ResumesWithUnwrappedResponse()
    {
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "human-agent-flow" };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "human", Kind = AgentflowNodeKind.HumanGate },
            new AgentflowNode { NodeId = "translator", Kind = AgentflowNodeKind.Agent, Name = "Translator" },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("human-to-agent", "human", "translator"),
            Edge("agent-to-output", "translator", "output"),
        };

        var workflow = _compiler.Compile(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { ["translator"] = CreateAgent("translator", "Translator") });

        Assert.NotNull(workflow);

        var input = new List<ChatMessage> { new(ChatRole.User, "Hello World!") };
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            input,
            cancellationToken: cancellationToken);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (evt is RequestInfoEvent request)
            {
                await run.SendResponseAsync(request.Request.CreateResponse(input));
            }
        }

        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        Assert.Contains(events, evt =>
            evt is ExecutorCompletedEvent completed &&
            completed.ExecutorId.Contains("Translator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compile_HumanGateBeforeConcurrentBlock_ResumesAndRunsParticipants()
    {
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "human-concurrent-flow" };
        var agentAClient = new CountingChatClient("agent a output");
        var agentBClient = new CountingChatClient("agent b output");
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "human", Kind = AgentflowNodeKind.HumanGate },
            new AgentflowNode { NodeId = "agent-a", Kind = AgentflowNodeKind.Agent, Name = "Agent A" },
            new AgentflowNode { NodeId = "agent-b", Kind = AgentflowNodeKind.Agent, Name = "Agent B" },
            new AgentflowNode
            {
                NodeId = "parallel",
                Kind = AgentflowNodeKind.ConcurrentBlock,
                Name = "Concurrent Block",
                ConfigJson = """{"participantNodeIds":["agent-a","agent-b"]}""",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("human-to-parallel", "human", "parallel"),
            Edge("parallel-to-output", "parallel", "output"),
        };

        var workflow = _compiler.Compile(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent>
            {
                ["agent-a"] = CreateAgent("agent-a", "Agent A", agentAClient),
                ["agent-b"] = CreateAgent("agent-b", "Agent B", agentBClient),
            });

        Assert.NotNull(workflow);

        var input = new List<ChatMessage> { new(ChatRole.User, "Hello World!") };
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            input,
            cancellationToken: cancellationToken);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (evt is RequestInfoEvent request)
            {
                await run.SendResponseAsync(request.Request.CreateResponse(input));
            }
        }

        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        Assert.True(agentAClient.TotalCalls > 0);
        Assert.True(agentBClient.TotalCalls > 0);
        Assert.Contains(events, evt =>
            evt is WorkflowOutputEvent output &&
            output.Data is List<ChatMessage> messages &&
            messages.Count >= 2 &&
            messages.Any(message => message.Text == "agent a output") &&
            messages.Any(message => message.Text == "agent b output"));
    }

    [Fact]
    public async Task Compile_ConcurrentBlock_ReassignsUpstreamAgentResponseAsUser()
    {
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "concurrent-role-flow" };
        var upstreamClient = new CapturingChatClient("Hello World!");
        var participantClient = new CapturingChatClient("translated output");
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "upstream", Kind = AgentflowNodeKind.Agent, Name = "Upstream" },
            new AgentflowNode { NodeId = "participant", Kind = AgentflowNodeKind.Agent, Name = "Participant" },
            new AgentflowNode
            {
                NodeId = "parallel",
                Kind = AgentflowNodeKind.ConcurrentBlock,
                Name = "Concurrent Block",
                ConfigJson = """{"participantNodeIds":["participant"]}""",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("upstream-to-parallel", "upstream", "parallel"),
            Edge("parallel-to-output", "parallel", "output"),
        };

        var workflow = _compiler.Compile(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent>
            {
                ["upstream"] = CreateAgent("upstream", "Upstream", upstreamClient),
                ["participant"] = CreateAgent("participant", "Participant", participantClient),
            });

        Assert.NotNull(workflow);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "initial input") },
            cancellationToken: cancellationToken);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        await foreach (var _ in run.WatchStreamAsync(cancellationToken))
        {
        }

        var participantInput = Assert.Single(participantClient.Messages);
        Assert.Equal(ChatRole.User, participantInput.Role);
        Assert.Equal("Hello World!", participantInput.Text);
    }

    [Fact]
    public async Task Compile_InputBeforeAgent_PassesInitialMessagesDownstream()
    {
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "input-agent-flow" };
        var chatClient = new CapturingChatClient("agent output");
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input, Name = "Input" },
            new AgentflowNode { NodeId = "agent", Kind = AgentflowNodeKind.Agent, Name = "Agent" },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("input-agent", "input", "agent", AgentflowEdgeKind.FanOut),
            Edge("agent-output", "agent", "output"),
        };

        var workflow = _compiler.Compile(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { ["agent"] = CreateAgent("agent", "Agent", chatClient) });

        Assert.NotNull(workflow);

        var input = new List<ChatMessage> { new(ChatRole.User, "Hello from Input") };
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            input,
            cancellationToken: TestContext.Current.CancellationToken);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var events = new List<WorkflowEvent>();
        await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken))
        {
            events.Add(_);
        }

        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        Assert.Contains(events, evt =>
            evt is ExecutorCompletedEvent completed &&
            completed.ExecutorId.Contains("Agent", StringComparison.Ordinal));
        Assert.Contains(chatClient.Messages, message => message.Text == "Hello from Input");
    }

    [Fact]
    public async Task Compile_SequentialAgents_ReassignsOnlyUpstreamAgentResponseAsUser()
    {
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "sequential-flow" };
        var firstChatClient = new CapturingChatClient("first agent output");
        var secondChatClient = new CapturingChatClient("second agent output");
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "agent-a", Kind = AgentflowNodeKind.Agent, Name = "Agent A" },
            new AgentflowNode { NodeId = "agent-b", Kind = AgentflowNodeKind.Agent, Name = "Agent B" },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("agent-a-to-agent-b", "agent-a", "agent-b"),
            Edge("agent-b-to-output", "agent-b", "output"),
        };

        var workflow = _compiler.Compile(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent>
            {
                ["agent-a"] = CreateAgent("agent-a", "Agent A", firstChatClient),
                ["agent-b"] = CreateAgent("agent-b", "Agent B", secondChatClient),
            });

        Assert.NotNull(workflow);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "initial user input") },
            cancellationToken: cancellationToken);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
        }

        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        var upstreamResponse = Assert.Single(secondChatClient.Messages);
        Assert.Equal(ChatRole.User, upstreamResponse.Role);
        Assert.Equal("first agent output", upstreamResponse.Text);
    }

    [Fact]
    public async Task Compile_WithSessionScope_InitializesInnerAgentSession()
    {
        var providerSessionState = new CapturingProviderSessionState();
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var sessionScope = new AgentflowAgentSessionScope(
            providerSessionState,
            projectId,
            "context-1",
            taskId);
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "scoped-flow" };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "agent", Kind = AgentflowNodeKind.Agent, Name = "Scoped Agent" },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("agent-to-output", "agent", "output"),
        };

        var workflow = _compiler.Compile(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { ["agent"] = CreateAgent("agent", "Scoped Agent") },
            sessionScope);

        Assert.NotNull(workflow);

        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "hello") },
            cancellationToken: TestContext.Current.CancellationToken);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken))
        {
        }

        Assert.Contains(providerSessionState.Calls, call =>
            call.ProjectId == projectId &&
            call.ContextId == "context-1");
    }

    [Fact]
    public async Task Compile_SummaryEnabledOutput_AppendsResultFromIncomingMessages()
    {
        var projectId = Guid.NewGuid();
        var modelProviderId = Guid.NewGuid();
        var summaryService = new RecordingSummaryService();
        var agentflow = new Agentflow
        {
            Id = Guid.NewGuid(),
            Name = "summary-flow",
            SummaryModelProviderId = modelProviderId,
        };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "agent", Kind = AgentflowNodeKind.Agent, Name = "Agent" },
            new AgentflowNode
            {
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
                Instructions = "Mention the verification result.",
                ConfigJson = """{"enableSummary":true}""",
            },
        };
        var workflow = _compiler.Compile(
            agentflow,
            nodes,
            [Edge("agent-output", "agent", "output")],
            new Dictionary<string, AIAgent>
            {
                ["agent"] = CreateAgent("agent", "Agent", new StubChatClient()),
            },
            sessionScope: null,
            executionTraceContext: null,
            new AgentflowSummaryContext(
                summaryService,
                modelProviderId,
                projectId,
                "context-1"));

        Assert.NotNull(workflow);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "workflow input") },
            cancellationToken: cancellationToken);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        List<ChatMessage>? output = null;
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            if (evt is WorkflowOutputEvent { Data: List<ChatMessage> messages })
            {
                output = messages;
            }
        }

        Assert.NotNull(output);
        Assert.Equal(2, output.Count);
        Assert.Equal("ok", output[0].Text);
        Assert.Equal("summary", output[1].Text);
        Assert.Equal(ChatRole.System, output[1].Role);
        Assert.Equal(Constants.DefaultAgentAuthor, output[1].AuthorName);
        Assert.Equal("result", output[1].AdditionalProperties!["type"]);

        var call = Assert.Single(summaryService.Calls);
        Assert.Equal(modelProviderId, call.ModelProviderId);
        Assert.Equal(projectId, call.ProjectId);
        Assert.Equal("context-1", call.ContextId);
        Assert.Equal("Mention the verification result.", call.CustomInstructions);
        Assert.Equal(["ok"], call.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task Compile_SummaryDisabledOutput_DoesNotCallSummaryService()
    {
        var summaryService = new RecordingSummaryService();
        var modelProviderId = Guid.NewGuid();
        var workflow = _compiler.Compile(
            new Agentflow
            {
                Id = Guid.NewGuid(),
                Name = "plain-flow",
                SummaryModelProviderId = modelProviderId,
            },
            [
                new AgentflowNode { NodeId = "agent", Kind = AgentflowNodeKind.Agent },
                new AgentflowNode
                {
                    NodeId = "output",
                    Kind = AgentflowNodeKind.Output,
                    ConfigJson = """{"enableSummary":false}""",
                },
            ],
            [Edge("agent-output", "agent", "output")],
            new Dictionary<string, AIAgent> { ["agent"] = CreateAgent("agent", "Agent") },
            sessionScope: null,
            executionTraceContext: null,
            new AgentflowSummaryContext(
                summaryService,
                modelProviderId,
                Guid.NewGuid(),
                "context-1"));

        Assert.NotNull(workflow);
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "input") },
            cancellationToken: TestContext.Current.CancellationToken);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken))
        {
        }

        Assert.Empty(summaryService.Calls);
    }

    private static AgentflowEdge Edge(
        string id,
        string source,
        string target,
        AgentflowEdgeKind kind = AgentflowEdgeKind.Direct)
    {
        return new AgentflowEdge
        {
            EdgeId = id,
            SourceNodeId = source,
            TargetNodeId = target,
            Kind = kind,
        };
    }

    private static AIAgent CreateAgent(string id, string name, IChatClient? chatClient = null)
    {
        return new ChatClientAgent(
            chatClient ?? new StubChatClient(),
            new ChatClientAgentOptions
            {
                Id = id,
                Name = name,
                ChatOptions = new ChatOptions { Instructions = "Test agent." },
            });
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }
    }

    private sealed class CountingChatClient(string responseText) : IChatClient
    {
        private int _totalCalls;

        public int TotalCalls => _totalCalls;

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _totalCalls);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _totalCalls);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
        }
    }

    private sealed class CapturingChatClient(string responseText) : IChatClient
    {
        public List<ChatMessage> Messages { get; } = [];

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Messages.AddRange(messages);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Messages.AddRange(messages);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
        }
    }

    private sealed class CapturingProviderSessionState : IProviderSessionState
    {
        public List<(Guid ProjectId, string ContextId)> Calls { get; } = [];

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId)
        {
            Calls.Add((projectId, contextId));
        }

        public bool TryGetProjectContext(AgentSession session, out Guid projectId, out string contextId)
        {
            var call = Calls.LastOrDefault();
            projectId = call.ProjectId;
            contextId = call.ContextId ?? string.Empty;
            return Calls.Count > 0;
        }
    }

    private sealed class CapturingExecutionTraceStore : IAgentflowNodeExecutionTraceStore
    {
        private readonly TaskCompletionSource<AgentflowTrace> _trace =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SaveAsync(AgentflowTrace trace, CancellationToken cancellationToken)
        {
            _trace.TrySetResult(trace);
            return Task.CompletedTask;
        }

        public Task<AgentflowTrace> WaitForTraceAsync() =>
            _trace.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private sealed class CollectingExecutionTraceStore : IAgentflowNodeExecutionTraceStore
    {
        private readonly object _lock = new();
        private readonly List<AgentflowTrace> _traces = [];

        public Task SaveAsync(AgentflowTrace trace, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _traces.Add(trace);
                Monitor.PulseAll(_lock);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentflowTrace>> WaitForCountAsync(int count)
        {
            return Task.Run<IReadOnlyList<AgentflowTrace>>(() =>
            {
                var timeout = TimeProvider.System.GetUtcNow().AddSeconds(2);
                lock (_lock)
                {
                    while (_traces.Count < count && TimeProvider.System.GetUtcNow() < timeout)
                    {
                        Monitor.Wait(_lock, TimeSpan.FromMilliseconds(20));
                    }

                    return _traces.ToList();
                }
            }, TestContext.Current.CancellationToken);
        }

        public IReadOnlyList<AgentflowTrace> GetTraces()
        {
            lock (_lock)
            {
                return _traces.ToList();
            }
        }
    }

    private sealed class RecordingSummaryService : IAgentTurnSummaryService
    {
        public List<SummaryCall> Calls { get; } = [];

        public Task<ChatMessage> CreateResultAsync(
            Guid modelProviderId,
            IReadOnlyList<ChatMessage> sourceMessages,
            Guid projectId,
            string contextId,
            string? customInstructions,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new SummaryCall(
                modelProviderId,
                sourceMessages,
                projectId,
                contextId,
                customInstructions));
            return Task.FromResult(AgentTurnSummaryService.CreateResultMessage("summary"));
        }
    }

    private sealed record SummaryCall(
        Guid ModelProviderId,
        IReadOnlyList<ChatMessage> Messages,
        Guid ProjectId,
        string ContextId,
        string? CustomInstructions);
}
