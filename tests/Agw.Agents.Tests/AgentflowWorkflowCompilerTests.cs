using Agw.Agents.Application.Agentflows;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Tests;

public class AgentflowWorkflowCompilerTests
{
    private readonly AgentflowWorkflowCompiler _compiler = new();

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

    [Fact]
    public void Compile_BlockNodeWithParticipants_ReturnsWorkflow()
    {
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "block-flow" };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "agent-a", Kind = AgentflowNodeKind.Agent, Name = "Agent A" },
            new AgentflowNode { NodeId = "agent-b", Kind = AgentflowNodeKind.Agent, Name = "Agent B" },
            new AgentflowNode
            {
                NodeId = "group",
                Kind = AgentflowNodeKind.GroupChatBlock,
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
    }
}
