using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;
using Agw.Agents.Execution.Agentflows.Context;
using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Agentflows.Workflows.Builders;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Middleware.Telemetry;
using Agw.Agents.Execution.Summaries;
using Agw.Projects.Application.History;
using Agw.Shared;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Tests;

[Collection(AgentflowExecutionTraceTestCollection.Name)]
public class AgentflowWorkflowCompilerTests : IDisposable
{
    private readonly AgentflowWorkflowCompiler _compiler = new(NullLogger<AgentflowWorkflowCompiler>.Instance);

    private readonly IDisposable _executionScope = ExecutionTestScopes
        .Scope(ExecutionTestScopes.Context(runtimeType: AgentRuntimeType.Agentflow))
        .Push();

    public void Dispose() => _executionScope.Dispose();

    [Fact]
    public void BlockBuilders_AreDedicatedInternalStaticTypes()
    {
        var assembly = typeof(AgentflowWorkflowCompiler).Assembly;
        var builderTypeNames = new[]
        {
            "Agw.Agents.Execution.Agentflows.Workflows.Builders.ConcurrentBlockBuilder",
            "Agw.Agents.Execution.Agentflows.Workflows.Builders.GroupChatBlockBuilder",
            "Agw.Agents.Execution.Agentflows.Workflows.Builders.HandoffBlockBuilder",
            "Agw.Agents.Execution.Agentflows.Workflows.Builders.MagenticBlockBuilder",
        };

        foreach (var builderTypeName in builderTypeNames)
        {
            var builderType = assembly.GetType(builderTypeName);

            Assert.NotNull(builderType);
            Assert.True(builderType.IsAbstract);
            Assert.True(builderType.IsSealed);
            Assert.False(builderType.IsPublic);

            var buildMethod = builderType.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(buildMethod);
            Assert.Equal(typeof(ExecutorBinding), buildMethod.ReturnType);
        }
    }

    [Fact]
    public void ApplyInstructions_WithInstructions_MarksReorderableContext()
    {
        var result = AgentflowMessageTransforms.ApplyInstructions([], "Follow the workflow instructions.");

        var instruction = Assert.Single(result);
        Assert.Equal(ChatRole.System, instruction.Role);
        Assert.Equal(Constants.DefaultInputAuthor, instruction.AuthorName);
        Assert.Equal(AgentRequestMessageSourceType.AIContextProvider, instruction.GetAgentRequestMessageSourceType());
    }

    [Theory]
    [InlineData("review.status", "approved", true)]
    [InlineData("review.score", "7", true)]
    [InlineData("review.ok", "true", true)]
    [InlineData("review.tags", "a", false)]
    [InlineData("review.note", "null", false)]
    [InlineData("review.missing", "x", false)]
    public void BuildCondition_ResultRule_ComparesValueAtPath(string path, string value, bool expected)
    {
        var resultMessage = new ChatMessage(
            ChatRole.Assistant,
            "{\"review\":{\"status\":\"Approved\",\"score\":7,\"ok\":true,\"tags\":[\"a\"],\"note\":null}}"
        )
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AgwMessageClassifier.TypeKey] = AgwMessageClassifier.ResultType,
                [AgwMessageClassifier.ResultFormatKey] = "json",
            },
        };
        var condition = _compiler.BuildCondition([new AgentflowConditionRule("result", value, path)]);

        Assert.NotNull(condition);
        Assert.Equal(expected, condition!([resultMessage]));
    }

    [Fact]
    public void BuildCondition_ResultRule_UsesLatestJsonResultAndRequiresOne()
    {
        var firstResult = CreateJsonResultMessage("{\"status\":\"approved\"}");
        var lastResult = CreateJsonResultMessage("{\"status\":\"rejected\"}");
        var condition = _compiler.BuildCondition([new AgentflowConditionRule("result", "rejected", "status")]);

        Assert.NotNull(condition);
        Assert.True(condition!([firstResult, lastResult]));
        Assert.False(condition([new ChatMessage(ChatRole.Assistant, "approved")]));
    }

    [Fact]
    public void BuildCondition_ResultRule_InvalidJson_ReturnsFalse()
    {
        var condition = _compiler.BuildCondition([new AgentflowConditionRule("result", "approved", "status")]);

        Assert.NotNull(condition);
        Assert.False(condition!([CreateJsonResultMessage("{invalid json")]));
    }

    [Fact]
    public void BuildCondition_TextRules_MatchExistingSemantics()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "review this"),
            new ChatMessage(ChatRole.Assistant, "approved") { AuthorName = "reviewer" },
        };
        var condition = _compiler.BuildCondition([
            new AgentflowConditionRule("contains", "approved", string.Empty),
            new AgentflowConditionRule("notContains", "declined", string.Empty),
            new AgentflowConditionRule("equals", "review this\napproved", string.Empty),
            new AgentflowConditionRule("author", "reviewer", string.Empty),
            new AgentflowConditionRule("role", "assistant", string.Empty),
            new AgentflowConditionRule("minMessages", "2", string.Empty),
        ]);

        Assert.NotNull(condition);
        Assert.True(condition!([.. messages]));
        Assert.False(condition([messages[0]]));
    }

    [Fact]
    public void CreateFeedbackLoopAgentInput_KeepsOnlyLatestUpstreamResultAndHumanReply()
    {
        var staleReview = new ChatMessage(ChatRole.Assistant, "stale review") { AuthorName = "general-agent" };
        var latestReview = new ChatMessage(ChatRole.Assistant, "latest review")
        {
            AuthorName = "general-agent",
            AdditionalProperties = new AdditionalPropertiesDictionary { ["nodeName"] = "Code Review" },
        };
        var humanReply = new ChatMessage(ChatRole.User, "yes") { AuthorName = "human" };

        var result = AgentflowMessageTransforms.CreateFeedbackLoopAgentInput([staleReview, latestReview, humanReply]);

        Assert.Equal(3, result.Count);
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Equal(AgentflowMessageTransforms.FeedbackLoopInstruction, result[0].Text);
        Assert.Equal(AgentRequestMessageSourceType.AIContextProvider, result[0].GetAgentRequestMessageSourceType());
        Assert.Equal(ChatRole.User, result[1].Role);
        Assert.Equal("latest review", result[1].Text);
        Assert.Equal("Code Review", result[1].AdditionalProperties!["nodeName"]);
        Assert.Equal(ChatRole.User, result[2].Role);
        Assert.Equal("human", result[2].AuthorName);
        Assert.Equal("yes", result[2].Text);
        Assert.DoesNotContain(result, message => message.Text == "stale review");
        Assert.Equal(ChatRole.Assistant, latestReview.Role);
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

        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "observable-flow" };
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "agent-node",
                Kind = AgentflowNodeKind.Agent,
                Name = "Node Alias",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[] { Edge("agent-output", "agent-node", "output") };
        var loggingMiddleware = new AgentTelemetryMiddleware(
            providerSessionState: null!,
            usageRecorder: null!,
            NullLogger<AgentTelemetryMiddleware>.Instance
        );
        var agent = CreateAgent("agent-id", "persisted-agent")
            .AsBuilder()
            .Use(runFunc: loggingMiddleware.RunAsync, runStreamingFunc: loggingMiddleware.RunStreamingAsync)
            .Build();

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { ["agent-node"] = agent }
        );

        Assert.NotNull(workflow);
        await using (
            var run = await InProcessExecution.RunStreamingAsync(
                workflow!,
                new List<ChatMessage> { new(ChatRole.User, "sensitive input") },
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken)) { }
        }

        Assert.Contains(activities, activity => activity.OperationName == "workflow.build");
        Assert.Contains(activities, activity => activity.OperationName == "workflow.session");
        Assert.Contains(activities, activity => activity.OperationName == "workflow_invoke");
        Assert.Contains(
            activities,
            activity => activity.OperationName.StartsWith("executor.process", StringComparison.Ordinal)
        );
        Assert.Contains(activities, activity => activity.OperationName == "edge_group.process");
        Assert.Contains(activities, activity => activity.OperationName == "message.send");
        Assert.DoesNotContain(
            activities.SelectMany(activity => activity.TagObjects),
            tag => tag.Key is "executor.input" or "executor.output" or "message.content"
        );
    }

    [Fact]
    public async Task Compile_AgentNode_PersistsNodeExecutionInputAndPersistentAgentName()
    {
        var store = new CapturingExecutionTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            store,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance
        );
        await collector.StartAsync(TestContext.Current.CancellationToken);

        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "persisted-flow" };
        var agentId = Guid.CreateVersion7();
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
        var execution = new AgentflowExecutionTraceContext(Guid.CreateVersion7(), "context-1", Guid.CreateVersion7());
        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            [Edge("agent-output", "agent-node", "output")],
            new Dictionary<string, AIAgent> { ["agent-node"] = CreateAgent("agent-id", "persisted-agent") },
            sessionScope: null,
            execution
        );

        Assert.NotNull(workflow);
        await using (
            var run = await InProcessExecution.RunStreamingAsync(
                workflow!,
                new List<ChatMessage> { new(ChatRole.User, "workflow input") },
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken)) { }
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
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "review-flow" };
        var agentId = "agent-a";
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = agentId,
                Kind = AgentflowNodeKind.Agent,
                Name = "Reviewer",
            },
            new AgentflowNode
            {
                NodeId = "adapter",
                Kind = AgentflowNodeKind.PromptAdapter,
                Instructions = "Use the previous output as a review brief.",
            },
            Gate("continue"),
            new AgentflowNode { NodeId = "checkpoint", Kind = AgentflowNodeKind.CheckpointMarker },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("e1", agentId, "adapter"),
            Edge("e2", "adapter", "human"),
            Edge("e3", "human", "checkpoint", choiceId: "continue"),
            Edge("e4", "checkpoint", "output"),
        };

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { [agentId] = CreateAgent(agentId, "Reviewer") }
        );

        Assert.NotNull(workflow);
        var mermaid = WorkflowVisualizer.ToMermaidString(workflow!);
        Assert.Contains("adapter", mermaid);
        Assert.Contains("human", mermaid);
        Assert.Contains("checkpoint", mermaid);
    }

    [Fact]
    public void Compile_FanOutAndFanInBarrierEdges_ReturnsWorkflow()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "parallel-flow" };
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "start",
                Kind = AgentflowNodeKind.PromptAdapter,
                RoutingStrategy = AgentflowRoutingStrategy.FanOut,
            },
            new AgentflowNode { NodeId = "left", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "right", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "join", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("fan-out-left", "start", "left"),
            Edge("fan-out-right", "start", "right"),
            Edge("fan-in-left", "left", "join", isFanInBarrier: true),
            Edge("fan-in-right", "right", "join", isFanInBarrier: true),
            Edge("to-output", "join", "output"),
        };

        var workflow = CompileWorkflow(agentflow, nodes, edges, new Dictionary<string, AIAgent>());

        Assert.NotNull(workflow);
        var mermaid = WorkflowVisualizer.ToMermaidString(workflow!);
        Assert.Contains("start", mermaid);
        Assert.Contains("join", mermaid);
    }

    [Fact]
    public async Task Compile_OrderedSwitch_ExecutesFirstMatchingCaseAndDefault()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "switch-flow" };
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                RoutingStrategy = AgentflowRoutingStrategy.Switch,
            },
            new AgentflowNode { NodeId = "first", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "second", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "fallback", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            Edge("listed-first", "input", "second", conditions: [new("contains", "approved", "")], switchCaseOrder: 1),
            Edge("ordered-first", "input", "first", conditions: [new("contains", "approved", "")]),
            Edge("default", "input", "fallback", isSwitchDefault: true),
        };
        var workflow = CompileWorkflow(agentflow, nodes, edges, new Dictionary<string, AIAgent>());

        Assert.NotNull(workflow);

        var matchingEvents = await ExecuteAsync(workflow!, "approved");
        Assert.Contains(matchingEvents, evt => HasChatInput(evt, "first"));
        Assert.Single(matchingEvents, evt => HasTurnTokenInput(evt, "first"));
        Assert.DoesNotContain(matchingEvents, evt => HasChatInput(evt, "second"));
        Assert.DoesNotContain(matchingEvents, evt => HasTurnTokenInput(evt, "second"));
        Assert.DoesNotContain(matchingEvents, evt => HasChatInput(evt, "fallback"));

        var defaultEvents = await ExecuteAsync(workflow!, "rejected");
        Assert.Contains(defaultEvents, evt => HasChatInput(evt, "fallback"));
        Assert.Single(defaultEvents, evt => HasTurnTokenInput(evt, "fallback"));
        Assert.DoesNotContain(defaultEvents, evt => HasChatInput(evt, "first"));
        Assert.DoesNotContain(defaultEvents, evt => HasChatInput(evt, "second"));
    }

    [Fact]
    public async Task Compile_DirectPredicates_RouteIndependently()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "direct-flow" };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "always", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "approved", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            Edge("always", "input", "always"),
            Edge("approved", "input", "approved", [new("contains", "approved", "")]),
        };
        var workflow = CompileWorkflow(agentflow, nodes, edges, new Dictionary<string, AIAgent>());

        Assert.NotNull(workflow);

        var matchingEvents = await ExecuteAsync(workflow!, "approved");
        Assert.Contains(matchingEvents, evt => HasChatInput(evt, "always"));
        Assert.Contains(matchingEvents, evt => HasChatInput(evt, "approved"));
        Assert.Single(matchingEvents, evt => HasTurnTokenInput(evt, "approved"));

        var unmatchedEvents = await ExecuteAsync(workflow!, "rejected");
        Assert.Contains(unmatchedEvents, evt => HasChatInput(evt, "always"));
        Assert.DoesNotContain(unmatchedEvents, evt => HasChatInput(evt, "approved"));
        Assert.DoesNotContain(unmatchedEvents, evt => HasTurnTokenInput(evt, "approved"));
    }

    [Fact]
    public async Task Compile_SwitchWithoutDefault_UnmatchedInputStopsRouting()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "switch-no-default" };
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                RoutingStrategy = AgentflowRoutingStrategy.Switch,
            },
            new AgentflowNode { NodeId = "approved", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[] { Edge("approved", "input", "approved", conditions: [new("contains", "approved", "")]) };
        var workflow = CompileWorkflow(agentflow, nodes, edges, new Dictionary<string, AIAgent>());

        Assert.NotNull(workflow);

        var events = await ExecuteAsync(workflow!, "rejected");
        Assert.DoesNotContain(events, evt => HasChatInput(evt, "approved"));
        Assert.DoesNotContain(events, evt => HasTurnTokenInput(evt, "approved"));
    }

    [Theory]
    [InlineData("none", 0)]
    [InlineData("alpha", 1)]
    [InlineData("alpha beta", 2)]
    public async Task Compile_ConditionalFanOut_SelectsEveryMatchingTarget(string input, int expectedConditionalTargets)
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "selection-flow" };
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                RoutingStrategy = AgentflowRoutingStrategy.FanOut,
            },
            new AgentflowNode { NodeId = "always", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "alpha", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "beta", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            Edge("always", "input", "always"),
            Edge("alpha", "input", "alpha", [new("contains", "alpha", "")]),
            Edge("beta", "input", "beta", [new("contains", "beta", "")]),
        };
        var workflow = CompileWorkflow(agentflow, nodes, edges, new Dictionary<string, AIAgent>());

        Assert.NotNull(workflow);

        var events = await ExecuteAsync(workflow!, input);
        Assert.Contains(events, evt => HasChatInput(evt, "always"));
        Assert.Single(events, evt => HasTurnTokenInput(evt, "always"));
        var conditionalTargets = events.Count(evt => HasChatInput(evt, "alpha") || HasChatInput(evt, "beta"));
        Assert.Equal(expectedConditionalTargets, conditionalTargets);
        var conditionalTargetTurns = events.Count(evt =>
            HasTurnTokenInput(evt, "alpha") || HasTurnTokenInput(evt, "beta")
        );
        Assert.Equal(expectedConditionalTargets, conditionalTargetTurns);
    }

    [Fact]
    public async Task Compile_InputAndUpstreamBarrier_WaitsThenExecutesTargetOnce()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "barrier-flow" };
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                RoutingStrategy = AgentflowRoutingStrategy.FanOut,
            },
            new AgentflowNode { NodeId = "a", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "b", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "c", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            Edge("input-a", "input", "a"),
            Edge("input-b", "input", "b", isFanInBarrier: true),
            Edge("a-b", "a", "b", isFanInBarrier: true),
            Edge("b-c", "b", "c"),
        };
        var workflow = CompileWorkflow(agentflow, nodes, edges, new Dictionary<string, AIAgent>());

        Assert.NotNull(workflow);

        var events = await ExecuteAsync(workflow!, "start");
        Assert.Single(events, evt => HasChatInput(evt, "b"));
        Assert.Single(events, evt => HasTurnTokenInput(evt, "b"));
        var cInput = Assert.Single(
            events
                .OfType<ExecutorInvokedEvent>()
                .Where(evt => evt.ExecutorId == "c")
                .Select(evt => evt.Data)
                .OfType<List<ChatMessage>>()
        );
        Assert.NotEmpty(cInput);
        var aIndex = events.ToList().FindIndex(evt => HasChatInput(evt, "a"));
        var cIndex = events.ToList().FindIndex(evt => HasChatInput(evt, "c"));
        Assert.True(aIndex >= 0 && aIndex < cIndex);
    }

    /// <summary>
    /// Input 节点原样转发输入：下游收到的消息保留 MessageId、创建时间与附加属性（包括 handoff 标记）。
    /// The Input node forwards input unchanged: downstream messages keep their MessageId, creation time and additional properties, including the handoff marker.
    /// </summary>
    [Fact]
    public async Task Compile_InputNodeStreaming_KeepsMessageIdentityAndProperties()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "input-identity" };
        var client = new CapturingChatClient("done");
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode
            {
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[] { Edge("input-agent", "input", "agent"), Edge("agent-output", "agent", "output") };
        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { ["agent"] = CreateAgent("agent", "Agent", client) }
        );
        Assert.NotNull(workflow);
        var handoff = new ChatMessage(ChatRole.User, "earlier request")
        {
            MessageId = Guid.CreateVersion7().ToString("D"),
        };
        ConversationHandoffMetadata.MarkHandoffMessage(handoff);
        var createdAt = new DateTimeOffset(2026, 11, 27, 0, 49, 45, TimeSpan.Zero);
        var input = new ChatMessage(ChatRole.User, "original request")
        {
            MessageId = Guid.CreateVersion7().ToString("D"),
            CreatedAt = createdAt,
            AdditionalProperties = new AdditionalPropertiesDictionary { ["custom"] = "kept" },
        };

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { handoff, input },
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            Assert.IsNotType<WorkflowErrorEvent>(evt);

        var receivedHandoff = Assert.Single(client.Messages, message => message.Text == "earlier request");
        Assert.Equal(handoff.MessageId, receivedHandoff.MessageId);
        Assert.True(ConversationHandoffMetadata.IsHandoffMessage(receivedHandoff));
        var receivedInput = Assert.Single(client.Messages, message => message.Text == "original request");
        Assert.Equal(input.MessageId, receivedInput.MessageId);
        Assert.Equal(createdAt, receivedInput.CreatedAt);
        Assert.Equal("kept", receivedInput.AdditionalProperties?["custom"]?.ToString());
    }

    /// <summary>
    /// Input 之后的 GroupChat 由两个 External Engine 参与者组成，历史在 Turn 结束时一次提交：提交成功，每个参与者的节点作用域各有一行用户输入。
    /// A GroupChat after the Input node has two External Engine participants and history commits once at turn end: the commit succeeds and each participant's node scope holds its own user input row.
    /// </summary>
    [Fact]
    public async Task Compile_GroupChatAfterInputNode_CommitsInputRowPerParticipantScope()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "group-chat-history" };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode
            {
                NodeId = "group",
                Kind = AgentflowNodeKind.GroupChatBlock,
                ConfigJson = AgentflowDefinitionJson.WriteOptions(new GroupChatBlockOptions(2)),
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[] { Edge("input-group", "input", "group"), Edge("group-output", "group", "output") };
        string[] memberIds = ["coding", "review"];
        var members = memberIds
            .Select(
                (memberId, index) =>
                    new AgentflowBlockMember
                    {
                        NodeId = "group",
                        MemberId = memberId,
                        Name = memberId,
                        Kind = AgentflowNodeKind.Agent,
                        SortOrder = index,
                    }
            )
            .ToArray();
        var agents = memberIds.ToDictionary(
            memberId => AgentflowBlockMemberKey.Create("group", memberId),
            AIAgent (_) =>
            {
                var history = fixture.CreateProvider(EngineKind.ACP);
                return HistoryTestFixture.Record(new HistoryNotifyingAgent(history), history);
            }
        );
        var sessionScope = new AgentflowAgentSessionScope(
            fixture.SessionState,
            fixture.ProjectId,
            HistoryTestFixture.ContextId,
            taskId: null,
            conversationId: fixture.ConversationId
        );
        var workflow = CompileWorkflow(agentflow, nodes, edges, agents, sessionScope, members: members);
        Assert.NotNull(workflow);
        var inputId = Guid.CreateVersion7();
        var input = new ChatMessage(ChatRole.User, "original request") { MessageId = inputId.ToString("D") };
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (fixture.BeginTurn(inputMessageId: inputId))
        {
            await using var run = await InProcessExecution.RunStreamingAsync(
                workflow!,
                new List<ChatMessage> { input },
                cancellationToken: cancellationToken
            );
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var evt in run.WatchStreamAsync(cancellationToken))
                Assert.IsNotType<WorkflowErrorEvent>(evt);
        }

        var scopes = memberIds.Select(memberId =>
            $"agentflow:{agentflow.Id:N}:node:{AgentflowBlockMemberKey.Create("group", memberId)}"
        );
        Assert.Equal(
            scopes.Select(scope => $"{scope}|{HistoryRecording.GetNodeInputRowId(scope, inputId)}").Order(),
            (await fixture.ReadAsync())
                .Where(row => row.GetText() == "original request")
                .Select(row => $"{row.HistoryScope}|{row.Id}")
                .Order()
        );
    }

    [Fact]
    public async Task Compile_CyclicBarrier_ReusesInputAcrossHumanGateIterations()
    {
        var (agentflow, nodes, edges) = CreateCyclicBarrierFlow();
        var workflow = CompileWorkflow(agentflow, nodes, edges, new Dictionary<string, AIAgent>());

        Assert.NotNull(workflow);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "original input") },
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        var requestCount = 0;
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (evt is RequestInfoEvent request)
            {
                requestCount++;
                Assert.True(requestCount <= 2, "The workflow did not exit after the latest human reply.");
                if (requestCount == 2)
                {
                    Assert.True(request.Request.TryGetDataAs<List<ChatMessage>>(out var requestMessages));
                    Assert.Contains(
                        requestMessages,
                        message => message.AuthorName == "human" && message.Text == "retry"
                    );
                }

                await run.SendResponseAsync(
                    CreateHumanGateResponse(request.Request, requestCount == 1 ? "retry" : "done")
                );
            }
        }

        Assert.Equal(2, requestCount);
        Assert.Equal(2, events.Count(evt => HasChatInput(evt, "lower")));
        Assert.Equal(2, events.Count(evt => HasTurnTokenInput(evt, "lower")));
        Assert.Single(events, evt => HasChatInput(evt, "output"));
        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
    }

    [Fact]
    public async Task Compile_CheckpointBeforeClearMessages_RehydratesDownstreamOnly()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "checkpoint-clear-loop" };
        var codingClient = new CapturingChatClient("implementation complete");
        var reviewClient = new CapturingChatClient("review complete");
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                RoutingStrategy = AgentflowRoutingStrategy.FanOut,
            },
            new AgentflowNode
            {
                NodeId = "coding",
                Kind = AgentflowNodeKind.Agent,
                Name = "Coding",
            },
            new AgentflowNode { NodeId = "checkpoint", Kind = AgentflowNodeKind.CheckpointMarker },
            new AgentflowNode { NodeId = "clear", Kind = AgentflowNodeKind.ClearMessages },
            new AgentflowNode
            {
                NodeId = "review",
                Kind = AgentflowNodeKind.Agent,
                Name = "Code Review",
            },
            Gate("retry", "done"),
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("input-coding", "input", "coding"),
            Edge("input-review", "input", "review", isFanInBarrier: true),
            Edge("coding-checkpoint", "coding", "checkpoint"),
            Edge("checkpoint-clear", "checkpoint", "clear"),
            Edge("clear-review", "clear", "review", isFanInBarrier: true),
            Edge("review-human", "review", "human"),
            Edge("retry", "human", "coding", choiceId: "retry"),
            Edge("done", "human", "output", choiceId: "done"),
        };
        Workflow? BuildWorkflow() =>
            CompileWorkflow(
                agentflow,
                nodes,
                edges,
                new Dictionary<string, AIAgent>
                {
                    ["coding"] = CreateAgent("coding", "Coding", codingClient),
                    ["review"] = CreateAgent("review", "Code Review", reviewClient),
                }
            );

        var workflow = BuildWorkflow();

        Assert.NotNull(workflow);
        var cancellationToken = TestContext.Current.CancellationToken;
        var checkpointManager = CheckpointManager.CreateInMemory();
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "original request") },
            checkpointManager,
            $"checkpoint-test-{Guid.CreateVersion7():N}",
            cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        var requestCount = 0;
        var checkpointRequestCount = 0;
        RequestInfoEvent? checkpointRequest = null;
        CheckpointInfo? markerCheckpoint = null;
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (
                evt is RequestInfoEvent request
                && request.Request.PortInfo.PortId == AgentflowWorkflowCompiler.GetCheckpointRequestPortId("checkpoint")
            )
            {
                checkpointRequestCount++;
                checkpointRequest = request;
            }

            if (
                evt is SuperStepCompletedEvent { CompletionInfo.Checkpoint: not null } completed
                && checkpointRequest != null
            )
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                Assert.Equal(0, reviewClient.TotalCalls);
                markerCheckpoint = completed.CompletionInfo.Checkpoint;
                Assert.True(checkpointRequest.Request.TryGetDataAs<List<ChatMessage>>(out var checkpointMessages));
                await run.SendResponseAsync(checkpointRequest.Request.CreateResponse(checkpointMessages!));
                checkpointRequest = null;
            }

            if (
                evt is RequestInfoEvent humanRequest
                && humanRequest.Request.PortInfo.PortId
                    != AgentflowWorkflowCompiler.GetCheckpointRequestPortId("checkpoint")
            )
            {
                requestCount++;
                await run.SendResponseAsync(CreateHumanGateResponse(humanRequest.Request, "done"));
            }
        }

        Assert.True(run.IsCheckpointingEnabled);
        Assert.NotNull(markerCheckpoint);
        Assert.Contains(markerCheckpoint, run.Checkpoints);
        Assert.Equal(1, codingClient.TotalCalls);
        Assert.Equal(1, reviewClient.TotalCalls);
        Assert.Equal(1, checkpointRequestCount);
        var reviewInput = Assert.Single(reviewClient.Messages);
        Assert.Equal("original request", reviewInput.Text);
        Assert.Equal(1, requestCount);
        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);

        var resumedWorkflow = BuildWorkflow();

        Assert.NotNull(resumedWorkflow);
        await using var resumedRun = await InProcessExecution.ResumeStreamingAsync(
            resumedWorkflow!,
            markerCheckpoint,
            checkpointManager,
            cancellationToken
        );
        await foreach (var evt in resumedRun.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (
                evt is RequestInfoEvent request
                && request.Request.PortInfo.PortId == AgentflowWorkflowCompiler.GetCheckpointRequestPortId("checkpoint")
            )
            {
                checkpointRequestCount++;
                Assert.True(request.Request.TryGetDataAs<List<ChatMessage>>(out var checkpointMessages));
                await resumedRun.SendResponseAsync(request.Request.CreateResponse(checkpointMessages!));
            }
            else if (evt is RequestInfoEvent humanRequest)
            {
                requestCount++;
                await resumedRun.SendResponseAsync(CreateHumanGateResponse(humanRequest.Request, "done"));
            }
        }

        Assert.Equal(1, codingClient.TotalCalls);
        Assert.Equal(2, reviewClient.TotalCalls);
        Assert.Equal(2, checkpointRequestCount);
        Assert.Equal(2, reviewClient.Messages.Count(message => message.Text == "original request"));
        Assert.Equal(2, requestCount);
        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
    }

    [Fact]
    public async Task Compile_Checkpoint_AggregatesMultiMessageAgentTurnIntoSingleRequest()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "checkpoint-multi-message" };
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                RoutingStrategy = AgentflowRoutingStrategy.FanOut,
            },
            new AgentflowNode
            {
                NodeId = "coding",
                Kind = AgentflowNodeKind.Agent,
                Name = "Coding",
            },
            new AgentflowNode { NodeId = "checkpoint", Kind = AgentflowNodeKind.CheckpointMarker },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("input-coding", "input", "coding"),
            Edge("coding-checkpoint", "coding", "checkpoint"),
            Edge("checkpoint-output", "checkpoint", "output"),
        };
        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent>
            {
                ["coding"] = CreateAgent("coding", "Coding", new ToolTranscriptChatClient()),
            }
        );

        Assert.NotNull(workflow);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "implement") },
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var checkpointRequestCount = 0;
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            if (
                evt is not RequestInfoEvent request
                || request.Request.PortInfo.PortId != AgentflowWorkflowCompiler.GetCheckpointRequestPortId("checkpoint")
            )
            {
                continue;
            }

            checkpointRequestCount++;
            Assert.True(request.Request.TryGetDataAs<List<ChatMessage>>(out var messages));
            await run.SendResponseAsync(request.Request.CreateResponse(messages!));
        }

        Assert.Equal(1, checkpointRequestCount);
    }

    [Fact]
    public async Task Compile_Checkpoint_AfterToolApprovalCreatesSingleRequest()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "checkpoint-tool-approval" };
        var worker = new ApprovalRequestAgent();
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "worker",
                Kind = AgentflowNodeKind.Agent,
                Name = "Worker",
            },
            new AgentflowNode { NodeId = "checkpoint", Kind = AgentflowNodeKind.CheckpointMarker },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            [Edge("worker-checkpoint", "worker", "checkpoint"), Edge("checkpoint-output", "checkpoint", "output")],
            new Dictionary<string, AIAgent> { ["worker"] = worker }
        );

        Assert.NotNull(workflow);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "change the code") },
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var checkpointRequestCount = 0;
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            if (evt is not RequestInfoEvent request)
            {
                continue;
            }

            if (request.Request.TryGetDataAs<ToolApprovalRequestContent>(out var approvalRequest))
            {
                await run.SendResponseAsync(
                    request.Request.CreateResponse(approvalRequest.CreateResponse(approved: true))
                );
                continue;
            }

            if (request.Request.PortInfo.PortId == AgentflowWorkflowCompiler.GetCheckpointRequestPortId("checkpoint"))
            {
                checkpointRequestCount++;
                Assert.True(request.Request.TryGetDataAs<List<ChatMessage>>(out var messages));
                await run.SendResponseAsync(request.Request.CreateResponse(messages!));
            }
        }

        Assert.Equal(2, worker.TotalCalls);
        Assert.Equal(1, checkpointRequestCount);
    }

    [Fact]
    public async Task Compile_CyclicBarrier_RestoresReusableInputFromCheckpoint()
    {
        var (agentflow, nodes, edges) = CreateCyclicBarrierFlow();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sessionId = $"durable-{Guid.CreateVersion7():N}";
        var firstStore = new DurableAgentflowCheckpointStore();
        var firstManager = CheckpointManager.CreateJson(firstStore);
        var firstWorkflow = CompileWorkflow(agentflow, nodes, edges, new Dictionary<string, AIAgent>());

        Assert.NotNull(firstWorkflow);

        await using (
            var firstRun = await InProcessExecution.RunStreamingAsync(
                firstWorkflow!,
                new List<ChatMessage> { new(ChatRole.User, "original input") },
                firstManager,
                sessionId,
                cancellationToken
            )
        )
        {
            await firstRun.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var evt in firstRun.WatchStreamAsync(cancellationToken))
            {
                if (
                    evt is SuperStepCompletedEvent
                    {
                        CompletionInfo.HasPendingRequests: true,
                        CompletionInfo.Checkpoint: not null,
                    }
                )
                {
                    await firstRun.CancelRunAsync();
                    break;
                }
            }
        }

        Assert.NotNull(firstStore.Latest);

        var restoredStore = new DurableAgentflowCheckpointStore(firstStore.Latest);
        var restoredManager = CheckpointManager.CreateJson(restoredStore);
        var checkpoint = await restoredManager.GetLatestCheckpointAsync(sessionId, cancellationToken);
        var restoredWorkflow = CompileWorkflow(agentflow, nodes, edges, new Dictionary<string, AIAgent>());

        Assert.NotNull(checkpoint);
        Assert.NotNull(restoredWorkflow);

        await using var restoredRun = await InProcessExecution.ResumeStreamingAsync(
            restoredWorkflow!,
            checkpoint!,
            restoredManager,
            cancellationToken
        );
        await restoredRun.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        var requestCount = 0;
        await foreach (var evt in restoredRun.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (evt is RequestInfoEvent request)
            {
                requestCount++;
                Assert.True(requestCount <= 2, "The restored workflow did not exit after the latest human reply.");
                await restoredRun.SendResponseAsync(
                    CreateHumanGateResponse(request.Request, requestCount == 1 ? "retry" : "done")
                );
            }
        }

        Assert.Equal(2, requestCount);
        Assert.DoesNotContain(events, evt => HasChatInput(evt, "input-lower.__agw_loop_barrier_source"));
        Assert.Single(events, evt => HasChatInput(evt, "lower"));
        Assert.Single(events, evt => HasChatInput(evt, "output"));
        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
    }

    [Theory]
    [InlineData(AgentflowNodeKind.ConcurrentBlock)]
    [InlineData(AgentflowNodeKind.GroupChatBlock)]
    [InlineData(AgentflowNodeKind.HandoffBlock)]
    [InlineData(AgentflowNodeKind.MagenticBlock)]
    public void Compile_BlockNodeWithParticipants_ReturnsWorkflow(AgentflowNodeKind blockKind)
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "block-flow" };
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "group",
                Kind = blockKind,
                Name = "GroupChat Room",
                ConfigJson = blockKind switch
                {
                    AgentflowNodeKind.GroupChatBlock => AgentflowDefinitionJson.WriteOptions(
                        new GroupChatBlockOptions(2)
                    ),
                    AgentflowNodeKind.MagenticBlock => AgentflowDefinitionJson.WriteOptions(
                        new MagenticBlockOptions(2, null, null, null)
                    ),
                    _ => AgentflowDefinitionJson.EmptyOptions,
                },
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[] { Edge("group-to-output", "group", "output") };
        var agents = new Dictionary<string, AIAgent>
        {
            ["group.agent-a"] = CreateAgent("agent-a", "Agent A"),
            ["group.agent-b"] = CreateAgent("agent-b", "Agent B"),
        };

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            agents,
            members:
            [
                Member("group", "agent-a", "Agent A", isManager: blockKind == AgentflowNodeKind.MagenticBlock),
                Member("group", "agent-b", "Agent B", sortOrder: 1),
            ]
        );

        Assert.NotNull(workflow);
        Assert.Contains("group", WorkflowVisualizer.ToMermaidString(workflow!));
    }

    [Fact]
    public async Task Compile_ConcurrentBlock_PersistsOnlyParticipantAgents()
    {
        var store = new CollectingExecutionTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            store,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance
        );
        await collector.StartAsync(TestContext.Current.CancellationToken);
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "parallel-trace-flow" };
        var agentAId = Guid.CreateVersion7();
        var agentBId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "parallel",
                Kind = AgentflowNodeKind.ConcurrentBlock,
                Name = "Parallel Block",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            [Edge("parallel-output", "parallel", "output")],
            new Dictionary<string, AIAgent>
            {
                ["parallel.agent-a"] = CreateAgent("agent-a", "persisted-agent-a"),
                ["parallel.agent-b"] = CreateAgent("agent-b", "persisted-agent-b"),
            },
            sessionScope: null,
            new AgentflowExecutionTraceContext(Guid.CreateVersion7(), "context-parallel", Guid.CreateVersion7()),
            members:
            [
                Member("parallel", "agent-a", "Agent A Node", relateId: agentAId),
                Member("parallel", "agent-b", "Agent B Node", sortOrder: 1, relateId: agentBId),
            ]
        );

        Assert.NotNull(workflow);
        await using (
            var run = await InProcessExecution.RunStreamingAsync(
                workflow!,
                new List<ChatMessage> { new(ChatRole.User, "parallel input") },
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken)) { }
        }

        var traces = await store.WaitForCountAsync(2);
        Assert.Equal(2, traces.Count);
        Assert.Contains(
            traces,
            trace => trace.NodeId == "parallel" && trace.AgentId == agentAId && trace.AgentName == "persisted-agent-a"
        );
        Assert.Contains(
            traces,
            trace => trace.NodeId == "parallel" && trace.AgentId == agentBId && trace.AgentName == "persisted-agent-b"
        );
        Assert.All(traces, trace => Assert.Equal(1, trace.Extra.AgentflowRevision));

        await collector.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Compile_BlockWithWorkflowAsAgentParticipant_PersistsOnlyNestedAgent()
    {
        var store = new CollectingExecutionTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            store,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance
        );
        await collector.StartAsync(TestContext.Current.CancellationToken);
        var execution = new AgentflowExecutionTraceContext(
            Guid.CreateVersion7(),
            "context-nested",
            Guid.CreateVersion7()
        );
        var nestedAgentId = Guid.CreateVersion7();
        var nestedAgentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "nested-flow" };
        var nestedWorkflow = CompileWorkflow(
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
            execution
        );
        Assert.NotNull(nestedWorkflow);

        var outerAgentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "outer-flow" };
        var outerWorkflow = CompileWorkflow(
            outerAgentflow,
            [
                new AgentflowNode
                {
                    NodeId = "parallel",
                    Kind = AgentflowNodeKind.ConcurrentBlock,
                    Name = "Parallel Block",
                },
                new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
            ],
            [Edge("parallel-output", "parallel", "output")],
            new Dictionary<string, AIAgent> { ["parallel.nested-participant"] = nestedWorkflow!.AsAIAgent() },
            sessionScope: null,
            execution,
            members:
            [
                Member(
                    "parallel",
                    "nested-participant",
                    "Nested Workflow Node",
                    relateId: nestedAgentflow.Id,
                    kind: AgentflowNodeKind.WorkflowAsAgent
                ),
            ]
        );
        Assert.NotNull(outerWorkflow);

        await using (
            var run = await InProcessExecution.RunStreamingAsync(
                outerWorkflow!,
                new List<ChatMessage> { new(ChatRole.User, "nested input") },
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken)) { }
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
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "human-agent-flow" };
        var nodes = new[]
        {
            Gate("continue"),
            new AgentflowNode
            {
                NodeId = "translator",
                Kind = AgentflowNodeKind.Agent,
                Name = "Translator",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("human-to-agent", "human", "translator", choiceId: "continue"),
            Edge("agent-to-output", "translator", "output"),
        };

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { ["translator"] = CreateAgent("translator", "Translator") }
        );

        Assert.NotNull(workflow);

        var input = new List<ChatMessage> { new(ChatRole.User, "Hello World!") };
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            input,
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (evt is RequestInfoEvent request)
            {
                await run.SendResponseAsync(CreateHumanGateResponse(request.Request, "continue"));
            }
        }

        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        Assert.Contains(
            events,
            evt =>
                evt is ExecutorCompletedEvent completed
                && completed.ExecutorId.Contains("Translator", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData("Proceed", true)]
    [InlineData("Stop", false)]
    public async Task RunAsync_HumanGateChoice_ExecutesOnlyTheSelectedPath(string choiceId, bool runsOutput)
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "human-choice-no-edge" };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            Gate("Proceed", "Stop"),
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("input-human", "input", "human"),
            Edge("proceed-output", "human", "output", choiceId: "Proceed"),
        };

        var workflow = CompileWorkflow(agentflow, nodes, edges, new Dictionary<string, AIAgent>());

        Assert.NotNull(workflow);
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "input") },
            cancellationToken: TestContext.Current.CancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            if (evt is RequestInfoEvent request)
                await run.SendResponseAsync(CreateHumanGateResponse(request.Request, choiceId));
        }

        Assert.Single(events.OfType<RequestInfoEvent>());
        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        Assert.Equal(runsOutput, events.OfType<ExecutorInvokedEvent>().Any(evt => evt.ExecutorId == "output"));
    }

    [Fact]
    public async Task Compile_HumanGateBeforeConcurrentBlock_ResumesAndRunsParticipants()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "human-concurrent-flow" };
        var agentAClient = new CountingChatClient("agent a output");
        var agentBClient = new CountingChatClient("agent b output");
        var nodes = new[]
        {
            Gate("continue"),
            new AgentflowNode
            {
                NodeId = "parallel",
                Kind = AgentflowNodeKind.ConcurrentBlock,
                Name = "Concurrent Block",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("human-to-parallel", "human", "parallel", choiceId: "continue"),
            Edge("parallel-to-output", "parallel", "output"),
        };

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent>
            {
                ["parallel.agent-a"] = CreateAgent("agent-a", "Agent A", agentAClient),
                ["parallel.agent-b"] = CreateAgent("agent-b", "Agent B", agentBClient),
            },
            members: [Member("parallel", "agent-a", "Agent A"), Member("parallel", "agent-b", "Agent B", sortOrder: 1)]
        );

        Assert.NotNull(workflow);

        var input = new List<ChatMessage> { new(ChatRole.User, "Hello World!") };
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            input,
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (evt is RequestInfoEvent request)
            {
                await run.SendResponseAsync(CreateHumanGateResponse(request.Request, "continue"));
            }
        }

        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        Assert.True(agentAClient.TotalCalls > 0);
        Assert.True(agentBClient.TotalCalls > 0);
        Assert.Contains(
            events,
            evt =>
                evt is WorkflowOutputEvent output
                && output.Data is List<ChatMessage> messages
                && messages.Count >= 2
                && messages.Any(message => message.Text == "agent a output")
                && messages.Any(message => message.Text == "agent b output")
        );
    }

    [Fact]
    public async Task Compile_ConcurrentBlock_ReassignsUpstreamAgentResponseAsUser()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "concurrent-role-flow" };
        var upstreamClient = new CapturingChatClient("Hello World!");
        var participantClient = new CapturingChatClient("translated output");
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "upstream",
                Kind = AgentflowNodeKind.Agent,
                Name = "Upstream",
            },
            new AgentflowNode
            {
                NodeId = "parallel",
                Kind = AgentflowNodeKind.ConcurrentBlock,
                Name = "Concurrent Block",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("upstream-to-parallel", "upstream", "parallel"),
            Edge("parallel-to-output", "parallel", "output"),
        };

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent>
            {
                ["upstream"] = CreateAgent("upstream", "Upstream", upstreamClient),
                ["parallel.participant"] = CreateAgent("participant", "Participant", participantClient),
            },
            members: [Member("parallel", "participant", "Participant")]
        );

        Assert.NotNull(workflow);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "initial input") },
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        await foreach (var _ in run.WatchStreamAsync(cancellationToken)) { }

        var participantInput = Assert.Single(participantClient.Messages);
        Assert.Equal(ChatRole.User, participantInput.Role);
        Assert.Equal("Hello World!", participantInput.Text);
    }

    [Fact]
    public async Task Compile_InputBeforeAgent_PassesInitialMessagesDownstream()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "input-agent-flow" };
        var chatClient = new CapturingChatClient("agent output");
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                Name = "Input",
            },
            new AgentflowNode
            {
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[] { Edge("input-agent", "input", "agent"), Edge("agent-output", "agent", "output") };

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { ["agent"] = CreateAgent("agent", "Agent", chatClient) }
        );

        Assert.NotNull(workflow);

        var input = new List<ChatMessage> { new(ChatRole.User, "Hello from Input") };
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            input,
            cancellationToken: TestContext.Current.CancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var events = new List<WorkflowEvent>();
        await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken))
        {
            events.Add(_);
        }

        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        Assert.Contains(
            events,
            evt =>
                evt is ExecutorCompletedEvent completed
                && completed.ExecutorId.Contains("Agent", StringComparison.Ordinal)
        );
        Assert.Contains(chatClient.Messages, message => message.Text == "Hello from Input");
    }

    [Fact]
    public async Task Compile_PromptAdapterBeforeAgent_ContinuesTurnIntoAgent()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "adapter-agent-flow" };
        var chatClient = new CapturingChatClient("agent output");
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                Name = "Input",
            },
            new AgentflowNode
            {
                NodeId = "adapter",
                Kind = AgentflowNodeKind.PromptAdapter,
                Instructions = "Extract the URL before continuing.",
            },
            new AgentflowNode
            {
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("input-adapter", "input", "adapter"),
            Edge("adapter-agent", "adapter", "agent"),
            Edge("agent-output", "agent", "output"),
        };

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { ["agent"] = CreateAgent("agent", "Agent", chatClient) }
        );

        Assert.NotNull(workflow);

        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "Visit https://example.com") },
            cancellationToken: TestContext.Current.CancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        Assert.Contains(chatClient.Messages, message => message.Text == "Extract the URL before continuing.");
        Assert.Contains(chatClient.Messages, message => message.Text == "Visit https://example.com");
    }

    [Fact]
    public async Task Compile_ClearMessagesBeforeAgent_DiscardsUpstreamMessagesAndContinuesTurn()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "clear-messages-flow" };
        var chatClient = new CapturingChatClient("agent output");
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                Name = "Input",
            },
            new AgentflowNode
            {
                NodeId = "adapter",
                Kind = AgentflowNodeKind.PromptAdapter,
                Instructions = "Add a second upstream message.",
            },
            new AgentflowNode { NodeId = "clear", Kind = AgentflowNodeKind.ClearMessages },
            new AgentflowNode
            {
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("input-adapter", "input", "adapter"),
            Edge("adapter-clear", "adapter", "clear"),
            Edge("clear-agent", "clear", "agent"),
            Edge("agent-output", "agent", "output"),
        };

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { ["agent"] = CreateAgent("agent", "Agent", chatClient) }
        );

        Assert.NotNull(workflow);

        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "Original input") },
            cancellationToken: TestContext.Current.CancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        Assert.Equal(1, chatClient.TotalCalls);
        Assert.Empty(chatClient.Messages);
    }

    [Fact]
    public async Task Compile_SequentialAgents_ReassignsOnlyUpstreamAgentResponseAsUser()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "sequential-flow" };
        var firstChatClient = new CapturingChatClient("first agent output");
        var secondChatClient = new CapturingChatClient("second agent output");
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "agent-a",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent A",
            },
            new AgentflowNode
            {
                NodeId = "agent-b",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent B",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("agent-a-to-agent-b", "agent-a", "agent-b"),
            Edge("agent-b-to-output", "agent-b", "output"),
        };

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent>
            {
                ["agent-a"] = CreateAgent("agent-a", "Agent A", firstChatClient),
                ["agent-b"] = CreateAgent("agent-b", "Agent B", secondChatClient),
            }
        );

        Assert.NotNull(workflow);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "initial user input") },
            cancellationToken: cancellationToken
        );
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
    public async Task Compile_SequentialAgents_ForwardsOnlyPortableUpstreamContent()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "portable-handoff-flow" };
        var secondChatClient = new CapturingChatClient("second agent output");
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "agent-a",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent A",
            },
            new AgentflowNode
            {
                NodeId = "agent-b",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent B",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("agent-a-to-agent-b", "agent-a", "agent-b"),
            Edge("agent-b-to-output", "agent-b", "output"),
        };

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent>
            {
                ["agent-a"] = CreateAgent("agent-a", "Agent A", new ToolTranscriptChatClient()),
                ["agent-b"] = CreateAgent("agent-b", "Agent B", secondChatClient),
            }
        );

        Assert.NotNull(workflow);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "initial user input") },
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
        }

        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        var handoff = Assert.Single(secondChatClient.Messages);
        Assert.Equal(ChatRole.User, handoff.Role);
        Assert.Equal("portable result", handoff.Text);
        Assert.All(handoff.Contents, content => Assert.True(content is TextContent or DataContent or UriContent));
    }

    [Fact]
    public async Task Compile_ExternalFunctionResult_ContinuesSameAgentWithToolMessage()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "external-tool-flow" };
        var agent = new ExternalFunctionCallAgent();
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            [Edge("agent-to-output", "agent", "output")],
            new Dictionary<string, AIAgent> { ["agent"] = agent }
        );

        Assert.NotNull(workflow);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "use external tool") },
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        var responded = false;
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (
                evt is RequestInfoEvent request
                && request.Request.TryGetDataAs<FunctionCallContent>(out var functionCall)
            )
            {
                responded = true;
                await run.SendResponseAsync(
                    request.Request.CreateResponse(new FunctionResultContent(functionCall.CallId, "external result"))
                );
            }
        }

        Assert.True(responded);
        Assert.True(agent.ReceivedFunctionResult);
        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
    }

    [Fact]
    public async Task Compile_HumanGateFeedbackLoop_CompactsReviewBeforeReturningToAgent()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "review-loop" };
        var codingClient = new RecordingChatClient("implementation complete");
        var reviewClient = new RecordingChatClient("review feedback: rename the variable");
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                Name = "Input",
            },
            new AgentflowNode
            {
                NodeId = "coding",
                Kind = AgentflowNodeKind.Agent,
                Name = "Coding",
                Instructions = "Implement requested changes.",
            },
            new AgentflowNode
            {
                NodeId = "review",
                Kind = AgentflowNodeKind.Agent,
                Name = "Code Review",
                Instructions = "Review the implementation.",
            },
            Gate("yes", "done"),
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            [
                Edge("input-coding", "input", "coding"),
                Edge("coding-review", "coding", "review"),
                Edge("review-human", "review", "human"),
                Edge("retry", "human", "coding", choiceId: "yes"),
                Edge("done", "human", "output", choiceId: "done"),
            ],
            new Dictionary<string, AIAgent>
            {
                ["coding"] = CreateAgent("coding", "Coding", codingClient),
                ["review"] = CreateAgent("review", "Code Review", reviewClient),
            }
        );

        Assert.NotNull(workflow);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "initial request") },
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        var requestCount = 0;
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (evt is RequestInfoEvent request)
            {
                requestCount++;
                await run.SendResponseAsync(
                    CreateHumanGateResponse(request.Request, requestCount == 1 ? "yes" : "done")
                );
            }
        }

        Assert.Equal(2, requestCount);
        Assert.Equal(2, codingClient.TotalCalls);
        Assert.Equal(2, reviewClient.TotalCalls);
        var secondCodingCall = codingClient.Calls[1];
        Assert.Contains(
            secondCodingCall,
            message =>
                message.Role == ChatRole.System && message.Text == AgentflowMessageTransforms.FeedbackLoopInstruction
        );
        Assert.Contains(
            secondCodingCall,
            message => message.Role == ChatRole.User && message.Text == "review feedback: rename the variable"
        );
        Assert.Contains(
            secondCodingCall,
            message => message.Role == ChatRole.User && message.AuthorName == "human" && message.Text == "yes"
        );
        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
    }

    [Fact]
    public async Task Compile_ToolApproval_DoesNotAdvanceDownstreamBeforeAgentCompletes()
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "tool-approval-flow" };
        var worker = new ApprovalRequestAgent();
        var reviewerClient = new CapturingChatClient("reviewed");
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "worker",
                Kind = AgentflowNodeKind.Agent,
                Name = "Worker",
            },
            new AgentflowNode
            {
                NodeId = "reviewer",
                Kind = AgentflowNodeKind.Agent,
                Name = "Reviewer",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            [Edge("worker-to-reviewer", "worker", "reviewer"), Edge("reviewer-to-output", "reviewer", "output")],
            new Dictionary<string, AIAgent>
            {
                ["worker"] = worker,
                ["reviewer"] = CreateAgent("reviewer", "Reviewer", reviewerClient),
            }
        );

        Assert.NotNull(workflow);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "change the code") },
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        var responded = false;
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (
                evt is RequestInfoEvent request
                && request.Request.TryGetDataAs<ToolApprovalRequestContent>(out var approvalRequest)
            )
            {
                Assert.Equal(0, reviewerClient.TotalCalls);
                responded = true;
                await run.SendResponseAsync(
                    request.Request.CreateResponse(approvalRequest.CreateResponse(approved: true))
                );
            }
        }

        Assert.True(responded);
        Assert.Equal(2, worker.TotalCalls);
        Assert.Equal(1, reviewerClient.TotalCalls);
        var reviewerInput = Assert.Single(reviewerClient.Messages);
        Assert.Equal("worker completed", reviewerInput.Text);
        Assert.DoesNotContain(
            reviewerInput.Contents,
            content => content is ToolApprovalRequestContent or ToolApprovalResponseContent
        );
        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
    }

    [Fact]
    public async Task CreateParticipant_UsesPersistedNodeIdForHistoryScope()
    {
        var providerSessionState = new CapturingProviderSessionState();
        var agentflowId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var member = new AgentflowBlockMember
        {
            AgentflowId = agentflowId,
            AgentflowRevision = 1,
            NodeId = "group",
            MemberId = "participant",
            Kind = AgentflowNodeKind.Agent,
            RelateId = Guid.CreateVersion7(),
            Name = "Participant",
            SortOrder = 0,
        };
        var context = new AgentflowBlockBuildContext(
            agentflowId,
            1,
            new AgentflowNode { NodeId = "group", Kind = AgentflowNodeKind.GroupChatBlock },
            [member],
            new Dictionary<string, AIAgent> { ["group.participant"] = CreateAgent("participant", "Participant") },
            new Dictionary<string, EngineKind> { ["group.participant"] = EngineKind.Maf },
            new Dictionary<string, AgentflowNodeResultOptions>(),
            new AgentflowAgentSessionScope(providerSessionState, projectId, "context-1", null),
            executionTraceContext: null,
            summaryContext: null,
            agentHostOptions: new AIAgentHostOptions()
        );
        var participant = AgentflowBlockBuildSupport.CreateParticipant(context, member, "group.participant");

        Assert.NotNull(participant);

        await participant!.RunAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Contains(
            providerSessionState.Calls,
            call => call.HistoryScope == $"agentflow:{agentflowId:N}:node:group.participant"
        );
    }

    [Fact]
    public async Task CreateParticipant_SummaryEnabled_AppendsResult()
    {
        var projectId = Guid.CreateVersion7();
        var modelProviderId = Guid.CreateVersion7();
        var summaryService = new RecordingSummaryService();
        var participantNode = new AgentflowNode { NodeId = "participant", Kind = AgentflowNodeKind.Agent };
        var blockNode = new AgentflowNode { NodeId = "group", Kind = AgentflowNodeKind.GroupChatBlock };
        var member = new AgentflowBlockMember
        {
            AgentflowId = Guid.CreateVersion7(),
            AgentflowRevision = 1,
            NodeId = blockNode.NodeId,
            MemberId = participantNode.NodeId,
            Kind = AgentflowNodeKind.Agent,
            RelateId = Guid.CreateVersion7(),
            Name = "Participant",
            SortOrder = 0,
        };
        var context = new AgentflowBlockBuildContext(
            member.AgentflowId,
            1,
            blockNode,
            [member],
            new Dictionary<string, AIAgent> { ["group.participant"] = CreateAgent("participant", "Participant") },
            new Dictionary<string, EngineKind> { ["group.participant"] = EngineKind.Maf },
            new Dictionary<string, AgentflowNodeResultOptions>
            {
                ["group.participant"] = new(modelProviderId, UseStructuredResult: false),
            },
            sessionScope: null,
            executionTraceContext: null,
            summaryContext: new AgentflowSummaryContext(summaryService, projectId, "context-1"),
            agentHostOptions: new AIAgentHostOptions()
        );
        var participant = AgentflowBlockBuildSupport.CreateParticipant(context, member, "group.participant");

        Assert.NotNull(participant);
        var response = await participant!.RunAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(["ok", "summary"], response.Messages.Select(message => message.Text));
        Assert.Equal("result", response.Messages[1].AdditionalProperties!["type"]);
        var call = Assert.Single(summaryService.Calls);
        Assert.Equal(modelProviderId, call.ModelProviderId);
        Assert.Equal(["hello", "ok"], call.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task RunStreamingAsync_AgentNodeWithExternalFunctionCall_SummarizesCompletedTurn()
    {
        var summaryService = new RecordingSummaryService();
        var agent = new AgentflowNodeScopedAgent(
            new ExternalFunctionCallAgent(),
            "agent",
            "Agent",
            instructions: null,
            sessionScope: null,
            resultOptions: new AgentflowNodeResultOptions(Guid.CreateVersion7(), UseStructuredResult: false),
            summaryContext: new AgentflowSummaryContext(summaryService, Guid.CreateVersion7(), "context-1")
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var firstUpdates = new List<AgentResponseUpdate>();
        await foreach (
            var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "check repository")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            firstUpdates.Add(update);
        }
        Assert.Empty(summaryService.Calls);

        var finalUpdates = new List<AgentResponseUpdate>();
        await foreach (
            var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("external-call", "tool result")])],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            finalUpdates.Add(update);
        }

        Assert.DoesNotContain(firstUpdates, update => AgwMessageClassifier.IsResult(update.ToAiMessage()!));
        var result = Assert.Single(finalUpdates, update => AgwMessageClassifier.IsResult(update.ToAiMessage()!));
        Assert.Equal("summary", result.Text);
        var call = Assert.Single(summaryService.Calls);
        Assert.Equal(["completed"], call.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task Compile_WithSessionScope_InitializesInnerAgentSession()
    {
        var providerSessionState = new CapturingProviderSessionState();
        var projectId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "scoped-flow" };
        var sessionScope = new AgentflowAgentSessionScope(providerSessionState, projectId, "context-1", taskId);
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Scoped Agent",
            },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[] { Edge("agent-to-output", "agent", "output") };

        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            edges,
            new Dictionary<string, AIAgent> { ["agent"] = CreateAgent("agent", "Scoped Agent") },
            sessionScope
        );

        Assert.NotNull(workflow);

        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "hello") },
            cancellationToken: TestContext.Current.CancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken)) { }

        Assert.Contains(
            providerSessionState.Calls,
            call =>
                call.ProjectId == projectId
                && call.ContextId == "context-1"
                && call.HistoryScope == $"agentflow:{agentflow.Id:N}:node:agent"
        );
    }

    [Theory]
    [InlineData(AgentflowRoutingStrategy.Direct, false, false)]
    [InlineData(AgentflowRoutingStrategy.Direct, false, true)]
    [InlineData(AgentflowRoutingStrategy.FanOut, false, false)]
    [InlineData(AgentflowRoutingStrategy.FanOut, false, true)]
    [InlineData(AgentflowRoutingStrategy.Direct, true, false)]
    [InlineData(AgentflowRoutingStrategy.Direct, true, true)]
    [InlineData(AgentflowRoutingStrategy.Switch, false, false)]
    [InlineData(AgentflowRoutingStrategy.Switch, false, true)]
    public async Task Compile_OutputAfterExternalFunctionResult_EmitsOnlyCompletedTurn(
        AgentflowRoutingStrategy routingStrategy,
        bool isFanInBarrier,
        bool enableSummary
    )
    {
        // Arrange
        var agent = new ExternalFunctionCallAgent();
        var summaryService = new RecordingSummaryService();
        var modelProviderId = Guid.CreateVersion7();
        var workflow = CompileWorkflow(
            new Agentflow { Id = Guid.CreateVersion7(), Name = "commit-push" },
            [
                new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
                new AgentflowNode { NodeId = "commit", Kind = AgentflowNodeKind.Agent },
                new AgentflowNode { NodeId = "clear", Kind = AgentflowNodeKind.ClearMessages },
                new AgentflowNode
                {
                    NodeId = "push",
                    Kind = AgentflowNodeKind.Agent,
                    Name = "Push And PR",
                    RoutingStrategy = routingStrategy,
                },
                new AgentflowNode
                {
                    NodeId = "output",
                    Kind = AgentflowNodeKind.Output,
                    SummaryModelProviderId = enableSummary ? modelProviderId : null,
                    ConfigJson = AgentflowDefinitionJson.WriteOptions(new OutputOptions(enableSummary)),
                },
            ],
            [
                Edge("input-commit", "input", "commit"),
                Edge("commit-clear", "commit", "clear"),
                Edge("clear-push", "clear", "push"),
                Edge(
                    "push-output",
                    "push",
                    "output",
                    conditions: routingStrategy == AgentflowRoutingStrategy.Switch
                        ? [new("minMessages", "1", "")]
                        : null,
                    isFanInBarrier: isFanInBarrier
                ),
            ],
            new Dictionary<string, AIAgent> { ["commit"] = CreateAgent("commit", "Commit"), ["push"] = agent },
            sessionScope: null,
            executionTraceContext: null,
            new AgentflowSummaryContext(summaryService, Guid.CreateVersion7(), "context-1")
        );
        Assert.NotNull(workflow);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            new List<ChatMessage> { new(ChatRole.User, "commit and push") },
            cancellationToken: cancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
            if (evt is RequestInfoEvent request && request.Request.TryGetDataAs<FunctionCallContent>(out var call))
            {
                await run.SendResponseAsync(
                    request.Request.CreateResponse(new FunctionResultContent(call.CallId, "external result"))
                );
            }
        }

        // Assert
        Assert.True(agent.ReceivedFunctionResult);
        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        var outputEvent = Assert.Single(events.OfType<WorkflowOutputEvent>(), evt => evt.Data is List<ChatMessage>);
        var output = Assert.IsType<List<ChatMessage>>(outputEvent.Data);
        var completedUpdateIndex = events.FindIndex(evt =>
            evt is AgentResponseUpdateEvent { Data: AgentResponseUpdate update } && update.Text == "completed"
        );
        Assert.True(completedUpdateIndex >= 0 && completedUpdateIndex < events.IndexOf(outputEvent));
        var expectedText = new[] { "checking repository", "completed" };
        Assert.Equal(
            enableSummary ? [.. expectedText, "summary"] : expectedText,
            output.Select(message => message.Text).Where(text => !string.IsNullOrEmpty(text))
        );
        if (enableSummary)
        {
            var summary = Assert.Single(summaryService.Calls);
            Assert.Equal(
                expectedText,
                summary.Messages.Select(message => message.Text).Where(text => !string.IsNullOrEmpty(text))
            );
        }
        else
        {
            Assert.Empty(summaryService.Calls);
        }
    }

    [Fact]
    public async Task Compile_SummaryEnabledOutput_AppendsResultFromIncomingMessages()
    {
        var projectId = Guid.CreateVersion7();
        var modelProviderId = Guid.CreateVersion7();
        var summaryService = new RecordingSummaryService();
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "summary-flow" };
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Agent",
            },
            new AgentflowNode
            {
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
                Instructions = "Mention the verification result.",
                SummaryModelProviderId = modelProviderId,
                ConfigJson = AgentflowDefinitionJson.WriteOptions(new OutputOptions(true)),
            },
        };
        var workflow = CompileWorkflow(
            agentflow,
            nodes,
            [Edge("agent-output", "agent", "output")],
            new Dictionary<string, AIAgent> { ["agent"] = CreateAgent("agent", "Agent", new StubChatClient()) },
            sessionScope: null,
            executionTraceContext: null,
            new AgentflowSummaryContext(summaryService, projectId, "context-1")
        );

        Assert.NotNull(workflow);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "workflow input") },
            cancellationToken: cancellationToken
        );
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
        Assert.Equal(ChatRole.Assistant, output[1].Role);
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
        var modelProviderId = Guid.CreateVersion7();
        var workflow = CompileWorkflow(
            new Agentflow { Id = Guid.CreateVersion7(), Name = "plain-flow" },
            [
                new AgentflowNode { NodeId = "agent", Kind = AgentflowNodeKind.Agent },
                new AgentflowNode
                {
                    NodeId = "output",
                    Kind = AgentflowNodeKind.Output,
                    ConfigJson = AgentflowDefinitionJson.WriteOptions(new OutputOptions(false)),
                },
            ],
            [Edge("agent-output", "agent", "output")],
            new Dictionary<string, AIAgent> { ["agent"] = CreateAgent("agent", "Agent") },
            sessionScope: null,
            executionTraceContext: null,
            new AgentflowSummaryContext(summaryService, Guid.CreateVersion7(), "context-1")
        );

        Assert.NotNull(workflow);
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow!,
            new List<ChatMessage> { new(ChatRole.User, "input") },
            cancellationToken: TestContext.Current.CancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        await foreach (var _ in run.WatchStreamAsync(TestContext.Current.CancellationToken)) { }

        Assert.Empty(summaryService.Calls);
    }

    private Workflow? CompileWorkflow(
        Agentflow agentflow,
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AIAgent> agents,
        AgentflowAgentSessionScope? sessionScope = null,
        AgentflowExecutionTraceContext? executionTraceContext = null,
        AgentflowSummaryContext? summaryContext = null,
        IReadOnlyList<AgentflowBlockMember>? members = null
    ) =>
        _compiler
            .Compile(
                agentflow,
                1,
                nodes,
                edges,
                members ?? [],
                agents,
                sessionScope,
                executionTraceContext,
                summaryContext
            )
            ?.Workflow;

    private static (Agentflow Agentflow, AgentflowNode[] Nodes, AgentflowEdge[] Edges) CreateCyclicBarrierFlow()
    {
        return (
            new Agentflow { Id = Guid.CreateVersion7(), Name = "revision-flow" },
            [
                new AgentflowNode
                {
                    NodeId = "input",
                    Kind = AgentflowNodeKind.Input,
                    RoutingStrategy = AgentflowRoutingStrategy.FanOut,
                },
                new AgentflowNode { NodeId = "upper", Kind = AgentflowNodeKind.PromptAdapter },
                new AgentflowNode { NodeId = "lower", Kind = AgentflowNodeKind.PromptAdapter },
                Gate("retry", "done"),
                new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
            ],
            [
                Edge("input-upper", "input", "upper"),
                Edge("input-lower", "input", "lower", isFanInBarrier: true),
                Edge("upper-lower", "upper", "lower", isFanInBarrier: true),
                Edge("lower-human", "lower", "human"),
                Edge("retry", "human", "upper", choiceId: "retry"),
                Edge("done", "human", "output", choiceId: "done"),
            ]
        );
    }

    private static AgentflowBlockMember Member(
        string nodeId,
        string memberId,
        string name,
        int sortOrder = 0,
        Guid? relateId = null,
        AgentflowNodeKind kind = AgentflowNodeKind.Agent,
        bool isManager = false
    ) =>
        new()
        {
            NodeId = nodeId,
            MemberId = memberId,
            Kind = kind,
            Name = name,
            SortOrder = sortOrder,
            RelateId = relateId ?? Guid.CreateVersion7(),
            IsManager = isManager,
        };

    private static AgentflowNode Gate(params string[] choices) =>
        new()
        {
            NodeId = "human",
            Kind = AgentflowNodeKind.HumanGate,
            RoutingStrategy = AgentflowRoutingStrategy.Choice,
            ConfigJson = AgentflowDefinitionJson.WriteOptions(
                new HumanGateOptions(
                    null,
                    choices.Select(choice => new AgentflowGateChoice(choice, choice)).ToArray(),
                    false
                )
            ),
        };

    private static ExternalResponse CreateHumanGateResponse(ExternalRequest request, string choiceId)
    {
        Assert.True(request.TryGetDataAs<List<ChatMessage>>(out var requestMessages));
        var responseMessages = requestMessages.ToList();
        responseMessages.Add(
            new ChatMessage(ChatRole.User, choiceId)
            {
                AuthorName = "human",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [AgwMessageClassifier.ChoiceIdKey] = choiceId,
                },
            }
        );
        return request.CreateResponse(responseMessages);
    }

    private static ChatMessage CreateJsonResultMessage(string json) =>
        new(ChatRole.Assistant, json)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AgwMessageClassifier.TypeKey] = AgwMessageClassifier.ResultType,
                [AgwMessageClassifier.ResultFormatKey] = "json",
            },
        };

    private static AgentflowEdge Edge(
        string id,
        string source,
        string target,
        IReadOnlyList<AgentflowConditionRule>? conditions = null,
        bool isFanInBarrier = false,
        bool isSwitchDefault = false,
        int switchCaseOrder = 0,
        string? choiceId = null
    )
    {
        return new AgentflowEdge
        {
            EdgeId = id,
            SourceNodeId = source,
            TargetNodeId = target,
            IsFanInBarrier = isFanInBarrier,
            IsSwitchDefault = isSwitchDefault,
            SwitchCaseOrder = switchCaseOrder,
            ChoiceId = choiceId,
            ConditionJson = AgentflowDefinitionJson.WriteConditions(conditions ?? []),
        };
    }

    private static async Task<IReadOnlyList<WorkflowEvent>> ExecuteAsync(Workflow workflow, string input)
    {
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            new List<ChatMessage> { new(ChatRole.User, input) },
            cancellationToken: TestContext.Current.CancellationToken
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }

    private static bool HasChatInput(WorkflowEvent evt, string executorId)
    {
        return evt is ExecutorInvokedEvent { ExecutorId: var invokedExecutorId, Data: List<ChatMessage> }
            && invokedExecutorId == executorId;
    }

    private static bool HasTurnTokenInput(WorkflowEvent evt, string executorId)
    {
        return evt is ExecutorInvokedEvent { ExecutorId: var invokedExecutorId, Data: TurnToken }
            && invokedExecutorId == executorId;
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
            }
        );
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }
    }

    private sealed class CountingChatClient(string responseText) : IChatClient
    {
        private int _totalCalls;

        public int TotalCalls => _totalCalls;

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _totalCalls);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _totalCalls);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
        }
    }

    private sealed class CapturingChatClient(string responseText) : IChatClient
    {
        private int _totalCalls;

        public int TotalCalls => _totalCalls;

        public List<ChatMessage> Messages { get; } = [];

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _totalCalls);
            Messages.AddRange(messages);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _totalCalls);
            Messages.AddRange(messages);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
        }
    }

    private sealed class RecordingChatClient(string responseText) : IChatClient
    {
        private int _totalCalls;

        public int TotalCalls => _totalCalls;

        public List<List<ChatMessage>> Calls { get; } = [];

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Record(messages);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Record(messages);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
        }

        private void Record(IEnumerable<ChatMessage> messages)
        {
            Interlocked.Increment(ref _totalCalls);
            Calls.Add(messages.Select(message => message.Clone()).ToList());
        }
    }

    private sealed class ToolTranscriptChatClient : IChatClient
    {
        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ChatResponse(CreateMessages()));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            foreach (var message in CreateMessages())
            {
                yield return new ChatResponseUpdate { Role = message.Role, Contents = message.Contents };
            }
        }

        private static List<ChatMessage> CreateMessages() =>
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>())
                        {
                            InformationalOnly = true,
                        },
                    ]
                ),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "tool result")]),
                new ChatMessage(ChatRole.Assistant, "portable result"),
            ];
    }

    private sealed class ExternalFunctionCallAgent : AIAgent
    {
        private int _callCount;

        public bool ReceivedFunctionResult { get; private set; }

        protected override string? IdCore => "external-function-agent";

        public override string? Name => "Agent";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ExternalFunctionCallSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new ExternalFunctionCallSession());

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
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                yield return new AgentResponseUpdate(ChatRole.Assistant, "checking repository")
                {
                    MessageId = "progress",
                };
                yield return new AgentResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents =
                    [
                        new FunctionCallContent("external-call", "external_tool", new Dictionary<string, object?>()),
                    ],
                };
                yield break;
            }

            ReceivedFunctionResult = messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .Any(content => content.CallId == "external-call");
            if (!ReceivedFunctionResult)
            {
                throw new InvalidOperationException("The external function result was not returned to the same agent.");
            }

            yield return new AgentResponseUpdate(ChatRole.Assistant, "completed");
        }

        private sealed class ExternalFunctionCallSession : AgentSession;
    }

    private sealed class ApprovalRequestAgent : AIAgent
    {
        private int _totalCalls;

        public int TotalCalls => _totalCalls;

        protected override string? IdCore => "approval-request-agent";

        public override string? Name => "Worker";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ApprovalRequestSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
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
            Interlocked.Increment(ref _totalCalls);
            var approvalResponse = messages
                .SelectMany(message => message.Contents)
                .OfType<ToolApprovalResponseContent>()
                .FirstOrDefault();
            if (approvalResponse != null)
            {
                yield return new AgentResponseUpdate(ChatRole.Assistant, "worker completed");
                yield break;
            }

            yield return new AgentResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new ToolApprovalRequestContent(
                        "approval-1",
                        new FunctionCallContent("call-1", "write_file", new Dictionary<string, object?>())
                    ),
                ],
            };
        }

        private sealed class ApprovalRequestSession : AgentSession;
    }

    private sealed class CapturingProviderSessionState : IProviderSessionState
    {
        public List<(Guid ProjectId, string ContextId, string? HistoryScope)> Calls { get; } = [];

        public void InitializeSessionState(AgentSession session, string contextId, Guid projectId)
        {
            Calls.Add((projectId, contextId, null));
        }

        public void InitializeSessionState(AgentSession session, string contextId, Guid projectId, string historyScope)
        {
            Calls.Add((projectId, contextId, historyScope));
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
        private readonly TaskCompletionSource<AgentflowTrace> _trace = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task SaveAsync(AgentflowTrace trace, CancellationToken cancellationToken)
        {
            _trace.TrySetResult(trace);
            return Task.CompletedTask;
        }

        public Task<AgentflowTrace> WaitForTraceAsync() => _trace.Task.WaitAsync(TestContext.Current.CancellationToken);
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
            return Task.Run<IReadOnlyList<AgentflowTrace>>(
                () =>
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
                },
                TestContext.Current.CancellationToken
            );
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
}
