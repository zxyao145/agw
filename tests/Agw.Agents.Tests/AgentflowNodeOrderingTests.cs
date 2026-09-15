using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class AgentflowNodeOrderingTests
{
    public static IEnumerable<object[]> Connections()
    {
        foreach (
            var kind in new[]
            {
                AgentflowNodeKind.Agent,
                AgentflowNodeKind.WorkflowAsAgent,
                AgentflowNodeKind.PromptAdapter,
                AgentflowNodeKind.ClearMessages,
                AgentflowNodeKind.HumanGate,
                AgentflowNodeKind.CheckpointMarker,
                AgentflowNodeKind.ConcurrentBlock,
                AgentflowNodeKind.GroupChatBlock,
                AgentflowNodeKind.HandoffBlock,
                AgentflowNodeKind.MagenticBlock,
            }
        )
        foreach (var route in Enum.GetValues<TestRoute>())
            yield return [kind, route];
    }

    [Theory]
    [MemberData(nameof(Connections))]
    public async Task Compile_CompletedNode_StartsDownstreamExactlyOnce(AgentflowNodeKind kind, TestRoute route)
    {
        // Arrange
        var source = new CountingClient();
        var target = new CountingClient();
        var workflow = Compile(kind, route, source, target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        // Act
        var events = new List<WorkflowEvent>();
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            new List<ChatMessage> { new(ChatRole.User, "start") },
            cancellationToken: timeout.Token
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        await foreach (var evt in run.WatchStreamAsync(timeout.Token))
        {
            events.Add(evt);
            if (evt is RequestInfoEvent request)
                await run.SendResponseAsync(
                    request.Request.CreateResponse(new List<ChatMessage> { new(ChatRole.User, "approved") })
                );
        }

        // Assert: an executor receiving a message is not evidence that its Agent actually ran.
        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        Assert.Equal(1, target.Calls);
        Assert.True(target.Finished);
        Assert.Single(events.OfType<WorkflowOutputEvent>(), evt => evt.Data is List<ChatMessage>);
    }

    public static IEnumerable<object[]> PausedConnections()
    {
        foreach (
            var kind in new[]
            {
                AgentflowNodeKind.Agent,
                AgentflowNodeKind.WorkflowAsAgent,
                AgentflowNodeKind.ConcurrentBlock,
                AgentflowNodeKind.GroupChatBlock,
                AgentflowNodeKind.HandoffBlock,
            }
        )
        foreach (var route in Enum.GetValues<TestRoute>())
            yield return [kind, route];
    }

    [Theory]
    [MemberData(nameof(PausedConnections))]
    public async Task Compile_PendingExternalTool_DoesNotStartDownstream(AgentflowNodeKind kind, TestRoute route)
    {
        // Arrange
        var source = new CountingClient { RequestExternalTool = true };
        var target = new CountingClient();
        var workflow = Compile(kind, route, source, target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var events = new List<WorkflowEvent>();
        var pending = new Dictionary<string, RequestInfoEvent>();
        var resumed = false;

        // Act
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            new List<ChatMessage> { new(ChatRole.User, "start") },
            cancellationToken: timeout.Token
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        await foreach (var evt in run.WatchStreamAsync(timeout.Token))
        {
            events.Add(evt);
            if (evt is RequestInfoEvent request)
                pending[request.Request.RequestId] = request;
            if (pending.Count > 0 && evt is SuperStepCompletedEvent { CompletionInfo.HasPendingMessages: false })
            {
                Assert.False(source.Finished);
                Assert.Equal(0, target.Calls);
                Assert.DoesNotContain(events, item => item is WorkflowOutputEvent { Data: List<ChatMessage> });
                var requestToResolve = Assert.Single(pending.Values);
                Assert.True(requestToResolve.Request.TryGetDataAs<FunctionCallContent>(out var call));
                await run.SendResponseAsync(
                    requestToResolve.Request.CreateResponse(new FunctionResultContent(call.CallId, "tool result"))
                );
                pending.Clear();
                resumed = true;
            }
        }

        // Assert
        Assert.DoesNotContain(events, evt => evt is WorkflowErrorEvent);
        Assert.True(resumed);
        Assert.True(source.Finished);
        Assert.Equal(1, target.Calls);
        Assert.Single(events.OfType<WorkflowOutputEvent>(), evt => evt.Data is List<ChatMessage>);
    }

    [Theory]
    [InlineData(AgentflowNodeKind.WorkflowAsAgent)]
    [InlineData(AgentflowNodeKind.ConcurrentBlock)]
    public async Task Compile_NestedHandledTool_PreservesObservationWithoutRequestingItAgain(AgentflowNodeKind kind)
    {
        var source = new CountingClient { HandledTool = true };
        var target = new CountingClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var run = await InProcessExecution.RunStreamingAsync(
            Compile(kind, TestRoute.Direct, source, target),
            new List<ChatMessage> { new(ChatRole.User, "start") },
            cancellationToken: timeout.Token
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var contents = new List<AIContent>();
        await foreach (var evt in run.WatchStreamAsync(timeout.Token))
        {
            Assert.IsNotType<WorkflowErrorEvent>(evt);
            Assert.IsNotType<RequestInfoEvent>(evt);
            if (evt is AgentResponseUpdateEvent update)
                contents.AddRange(update.Update.Contents);
        }
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, target.Calls);
        Assert.Contains(contents, content => content is FunctionCallContent { CallId: "handled" });
        Assert.Contains(contents, content => content is FunctionResultContent { CallId: "handled" });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Compile_FanIn_WaitsForEveryCompletedPredecessor(bool restore, bool emptySibling)
    {
        var target = new CountingClient();
        var definition = new Agentflow { Id = Guid.NewGuid(), Name = "join" };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode
            {
                NodeId = "fast",
                Kind = emptySibling ? AgentflowNodeKind.ClearMessages : AgentflowNodeKind.PromptAdapter,
            },
            new AgentflowNode { NodeId = "waiting", Kind = AgentflowNodeKind.HumanGate },
            new AgentflowNode { NodeId = "target", Kind = AgentflowNodeKind.Agent },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            Edge("input", "fast", AgentflowEdgeKind.FanOut),
            Edge("input", "waiting", AgentflowEdgeKind.FanOut),
            Edge("fast", "target", AgentflowEdgeKind.FanInBarrier),
            Edge("waiting", "target", AgentflowEdgeKind.FanInBarrier),
            Edge("target", "output"),
        };
        Workflow Build() =>
            new AgentflowWorkflowCompiler().Compile(
                definition,
                nodes,
                edges,
                new Dictionary<string, AIAgent> { ["target"] = Agent(target, "target") }
            )!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var store = new DurableAgentflowCheckpointStore();
        var manager = CheckpointManager.CreateJson(store);
        var sessionId = Guid.NewGuid().ToString();
        RequestInfoEvent? pending = null;
        var completed = false;
        await using (
            var run = await InProcessExecution.RunStreamingAsync(
                Build(),
                new List<ChatMessage> { new(ChatRole.User, "start") },
                manager,
                sessionId,
                timeout.Token
            )
        )
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var evt in run.WatchStreamAsync(timeout.Token))
            {
                Assert.IsNotType<WorkflowErrorEvent>(evt);
                if (evt is RequestInfoEvent request)
                    pending = request;
                if (pending != null && evt is SuperStepCompletedEvent { CompletionInfo.HasPendingMessages: false })
                {
                    Assert.Equal(0, target.Calls);
                    Assert.False(completed);
                    if (restore)
                    {
                        await run.CancelRunAsync();
                        break;
                    }
                    await run.SendResponseAsync(
                        pending.Request.CreateResponse(new List<ChatMessage> { new(ChatRole.User, "approved") })
                    );
                    pending = null;
                }
                if (evt is WorkflowOutputEvent { Data: List<ChatMessage> })
                    completed = true;
            }
        }
        if (restore)
        {
            Assert.NotNull(store.Latest);
            var restoredManager = CheckpointManager.CreateJson(new DurableAgentflowCheckpointStore(store.Latest));
            var checkpoint = await restoredManager.GetLatestCheckpointAsync(sessionId, timeout.Token);
            await using var run = await InProcessExecution.ResumeStreamingAsync(
                Build(),
                checkpoint!,
                restoredManager,
                timeout.Token
            );
            await run.SendResponseAsync(
                pending!.Request.CreateResponse(new List<ChatMessage> { new(ChatRole.User, "approved") })
            );
            await foreach (var evt in run.WatchStreamAsync(timeout.Token))
            {
                Assert.IsNotType<WorkflowErrorEvent>(evt);
                if (evt is WorkflowOutputEvent { Data: List<ChatMessage> })
                {
                    Assert.False(completed);
                    completed = true;
                }
            }
        }
        Assert.True(completed);
        Assert.Equal(1, target.Calls);
        Assert.Equal(emptySibling ? 1 : 2, target.LastInput.Count);
        Assert.Contains(target.LastInput, message => message.Text == "approved");
        if (!emptySibling)
            Assert.Contains(target.LastInput, message => message.Text == "start");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compile_FailedOrCancelledSource_DoesNotStartDownstream(bool cancel)
    {
        var source = new CountingClient { Fail = true, Cancel = cancel };
        var target = new CountingClient();
        var workflow = Compile(AgentflowNodeKind.Agent, TestRoute.Direct, source, target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var outputs = 0;
        try
        {
            await using var run = await InProcessExecution.RunStreamingAsync(
                workflow,
                new List<ChatMessage> { new(ChatRole.User, "start") },
                cancellationToken: timeout.Token
            );
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (var evt in run.WatchStreamAsync(timeout.Token))
                if (evt is WorkflowOutputEvent { Data: List<ChatMessage> })
                    outputs++;
        }
        catch (OperationCanceledException) when (cancel) { }
        Assert.Equal(1, source.Calls);
        Assert.False(source.Finished);
        Assert.Equal(0, target.Calls);
        Assert.Equal(0, outputs);
    }

    private static Workflow Compile(
        AgentflowNodeKind kind,
        TestRoute route,
        CountingClient source,
        CountingClient target
    )
    {
        var nodes = new List<AgentflowNode>
        {
            new() { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new() { NodeId = "source", Kind = kind },
            new() { NodeId = "target", Kind = AgentflowNodeKind.Agent },
            new() { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var agents = new Dictionary<string, AIAgent>
        {
            ["source"] = Agent(source, "source"),
            ["target"] = Agent(target, "target"),
        };
        if (
            kind
            is AgentflowNodeKind.ConcurrentBlock
                or AgentflowNodeKind.GroupChatBlock
                or AgentflowNodeKind.HandoffBlock
                or AgentflowNodeKind.MagenticBlock
        )
        {
            nodes[1].ConfigJson = """{"participantNodeIds":["a","b"],"maxRounds":1,"requirePlanSignoff":false}""";
            nodes.AddRange([
                new() { NodeId = "a", Kind = AgentflowNodeKind.Agent },
                new() { NodeId = "b", Kind = AgentflowNodeKind.Agent },
            ]);
            if (kind == AgentflowNodeKind.MagenticBlock)
                source.ReplyText =
                    """{"is_request_satisfied":{"answer":true,"reason":"done"},"is_in_loop":{"answer":false,"reason":"done"},"is_progress_being_made":{"answer":true,"reason":"done"},"next_speaker":{"answer":"source.b","reason":"done"},"instruction_or_question":{"answer":"done","reason":"done"}}""";
            agents["a"] = Agent(source, "a");
            agents["b"] = Agent(new CountingClient(), "b");
        }
        if (kind == AgentflowNodeKind.WorkflowAsAgent)
        {
            var nested = new AgentflowWorkflowCompiler().Compile(
                new Agentflow { Id = Guid.NewGuid(), Name = "nested" },
                [
                    new AgentflowNode { NodeId = "child", Kind = AgentflowNodeKind.Agent },
                    new AgentflowNode { NodeId = "child-output", Kind = AgentflowNodeKind.Output },
                ],
                [Edge("child", "child-output")],
                new Dictionary<string, AIAgent> { ["child"] = Agent(source, "child") }
            )!;
            agents["source"] = nested.AsAIAgent();
        }
        var edges = new List<AgentflowEdge> { Edge("input", "source"), Edge("target", "output") };
        var outgoing = Edge(
            "source",
            "target",
            route switch
            {
                TestRoute.FanOut => AgentflowEdgeKind.FanOut,
                TestRoute.FanIn => AgentflowEdgeKind.FanInBarrier,
                TestRoute.SwitchCase => AgentflowEdgeKind.SwitchCase,
                TestRoute.SwitchDefault => AgentflowEdgeKind.SwitchDefault,
                _ => AgentflowEdgeKind.Direct,
            }
        );
        if (route is TestRoute.DirectPredicate or TestRoute.SwitchCase)
            outgoing.ConditionJson = """{"always":true}""";
        if (route == TestRoute.SwitchCase)
            outgoing.ConfigJson = """{"switchCaseOrder":0}""";
        if (route == TestRoute.SwitchDefault)
        {
            nodes.Add(new() { NodeId = "unmatched", Kind = AgentflowNodeKind.Output });
            var unmatched = Edge("source", "unmatched", AgentflowEdgeKind.SwitchCase);
            unmatched.ConditionJson = """{"always":false}""";
            unmatched.ConfigJson = """{"switchCaseOrder":0}""";
            edges.Add(unmatched);
        }
        edges.Add(outgoing);
        return new AgentflowWorkflowCompiler().Compile(
            new Agentflow { Id = Guid.NewGuid(), Name = "ordering" },
            nodes,
            edges,
            agents
        )!;
    }

    private static AgentflowEdge Edge(
        string source,
        string target,
        AgentflowEdgeKind kind = AgentflowEdgeKind.Direct
    ) =>
        new()
        {
            EdgeId = source + "-" + target,
            SourceNodeId = source,
            TargetNodeId = target,
            Kind = kind,
        };

    private static AIAgent Agent(IChatClient client, string id) =>
        new ChatClientAgent(
            client,
            new ChatClientAgentOptions
            {
                Id = id,
                Name = id,
                UseProvidedChatClientAsIs = true,
            }
        );

    public enum TestRoute
    {
        Direct,
        DirectPredicate,
        FanOut,
        FanIn,
        SwitchCase,
        SwitchDefault,
    }

    private sealed class CountingClient : IChatClient
    {
        public int Calls { get; private set; }
        public bool Finished { get; private set; }
        public bool RequestExternalTool { get; init; }
        public bool HandledTool { get; init; }
        public string ReplyText { get; set; } = "completed";
        public bool Fail { get; init; }
        public bool Cancel { get; init; }
        public List<ChatMessage> LastInput { get; private set; } = [];

        public void Dispose() { }

        public object? GetService(Type type, object? key = null) => type.IsInstanceOfType(this) ? this : null;

        private ChatMessage Respond()
        {
            Calls++;
            if (RequestExternalTool && Calls == 1)
                return new(
                    ChatRole.Assistant,
                    [
                        new TextContent("partial"),
                        new FunctionCallContent("external-call", "external-tool", new Dictionary<string, object?>()),
                    ]
                );
            Finished = true;
            return new(ChatRole.Assistant, ReplyText);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ChatResponse(Respond()));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            LastInput = messages.ToList();
            if (Fail)
            {
                Calls++;
                yield return new(ChatRole.Assistant, "partial");
                if (Cancel)
                    throw new OperationCanceledException(cancellationToken);
                throw new InvalidOperationException("source failed");
            }
            if (HandledTool)
            {
                yield return new(
                    ChatRole.Assistant,
                    [new FunctionCallContent("handled", "tool", new Dictionary<string, object?>())]
                );
                yield return new(ChatRole.Tool, [new FunctionResultContent("handled", "result")]);
            }
            var response = Respond();
            yield return new(response.Role, response.Contents) { MessageId = $"message-{Calls}" };
        }
    }
}
