using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public partial class AgentflowRuntimeServiceTests
{
    [Fact]
    public async Task ExecuteStreamingAsync_UpdatesAndOutput_PreservesOrderAndSingleTerminalEvent()
    {
        var agent = new ScriptedAgent(["first", "second"]);
        var fixture = CreateCharacterizationFixture([AgentflowNodeKind.Agent, AgentflowNodeKind.Output], _ => agent);

        var messages = await CollectAsync(
            fixture.Service.ExecuteStreamingAsync(fixture.Flow.Id, "input", TestContext.Current.CancellationToken)
        );

        Assert.Equal(["input", "first", "second", "firstsecond", "turn-finished"], messages.Select(MessageShape));
        Assert.Equal("completed", messages[^1].AdditionalProperties!["status"]);
        Assert.Equal(1, agent.DisposeCount);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesAndOutput_ReturnsOnlyWorkflowOutput()
    {
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => new ScriptedAgent(["first", "second"])
        );
        var taskId = Guid.CreateVersion7();

        var result = await fixture.Service.ExecuteAsync(
            fixture.Flow.Id,
            taskId,
            "input",
            TestContext.Current.CancellationToken,
            contextId: " context "
        );

        Assert.NotNull(result);
        Assert.Equal(["input", "firstsecond", "firstsecond"], result.Messages.Select(MessageShape));
        Assert.Equal("context", result.ContextId);
        Assert.Equal(taskId.ToString("D"), result.TaskId);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_WorkflowFailure_EmitsErrorThenFinishedAndDisposes()
    {
        var agent = new ScriptedAgent([], fail: true);
        var fixture = CreateCharacterizationFixture([AgentflowNodeKind.Agent, AgentflowNodeKind.Output], _ => agent);

        var messages = await CollectAsync(
            fixture.Service.ExecuteStreamingAsync(fixture.Flow.Id, "input", TestContext.Current.CancellationToken)
        );

        Assert.Equal(["input", "workflow-error", "turn-finished"], messages.Select(MessageShape));
        Assert.StartsWith(
            "Error invoking handler for ",
            Assert.IsType<AgwErrorContent>(Assert.Single(messages[1].Contents)).Content
        );
        Assert.Equal(1, agent.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteStreamingAsync_HumanGateRejectedOrUnavailable_StopsBeforeAgent(bool unavailable)
    {
        var agent = new ScriptedAgent(["must not run"]);
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.HumanGate, AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => agent
        );

        var messages = await CollectAsync(
            fixture.Service.ExecuteStreamingAsync(
                fixture.Flow.Id,
                "input",
                TestContext.Current.CancellationToken,
                humanGateApprovalHandler: unavailable ? null : new FixedApprovalHandler(false)
            )
        );

        Assert.Equal(
            unavailable
                ? ["input", "human-gate-unavailable", "turn-finished"]
                : ["input", "human-gate-request", "human-gate-rejected", "turn-finished"],
            messages.Select(MessageShape)
        );
        Assert.Empty(agent.Inputs);
        Assert.Equal(1, agent.DisposeCount);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_HumanGateApproved_PreservesRequestPayloadAndTerminalSequence()
    {
        var agent = new ScriptedAgent(["done"]);
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.HumanGate, AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => agent
        );
        fixture.Nodes[0].ConfigJson = """{"humanMode":" review ","humanPrompt":" Approve now "}""";

        var messages = await CollectAsync(
            fixture.Service.ExecuteStreamingAsync(
                fixture.Flow.Id,
                "input",
                TestContext.Current.CancellationToken,
                humanGateApprovalHandler: new FixedApprovalHandler(true, " accepted ")
            )
        );

        Assert.Equal(["input", "human-gate-request", "turn-finished"], messages.Select(MessageShape));
        Assert.Equal("review", messages[1].AdditionalProperties!["mode"]);
        Assert.Equal("Approve now", messages[1].AdditionalProperties!["prompt"]);
        Assert.Equal("input", messages[1].AdditionalProperties!["inputPreview"]);
        Assert.Empty(agent.Inputs);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_ToolApprovalWithoutChannel_EmitsUnavailableThenFinished()
    {
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => new ApprovalRequestAgent()
        );

        var messages = await CollectAsync(
            fixture.Service.ExecuteStreamingAsync(fixture.Flow.Id, "input", TestContext.Current.CancellationToken)
        );

        Assert.Equal(["input", "", "tool-approval-unavailable", "turn-finished"], messages.Select(MessageShape));
        Assert.Equal("approval-1", messages[2].AdditionalProperties!["requestId"]);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_ConsumerStopsEarly_AwaitsAgentDisposal()
    {
        var agent = new ScriptedAgent(["first", "second"]);
        var fixture = CreateCharacterizationFixture([AgentflowNodeKind.Agent, AgentflowNodeKind.Output], _ => agent);

        await using (
            var enumerator = fixture
                .Service.ExecuteStreamingAsync(fixture.Flow.Id, "input", TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken)
        )
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("input", MessageShape(enumerator.Current));
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("first", MessageShape(enumerator.Current));
        }

        Assert.Equal(1, agent.DisposeCount);
    }

    [Fact]
    public async Task ExecuteDurableSegmentAsync_FirstSegment_CompletesWithoutTerminalMessage()
    {
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => new ScriptedAgent(["first", "second"])
        );
        var manifest = CreateManifest(fixture.Flow.Id);
        var sink = new RecordingSegmentSink();

        var result = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionSegmentStatus.Completed, result.Status);
        Assert.Equal(manifest.ExecutionId, result.ExecutionId);
        Assert.Equal(0, result.SegmentIndex);
        Assert.Equal(["input", "first", "second", "firstsecond"], sink.Messages.Select(MessageShape));
        Assert.Empty(result.PendingInteractions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteDurableSegmentAsync_HumanGateCheckpoint_RestoresPersistedResponse(bool approved)
    {
        var fixture = CreateCharacterizationFixture([
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.Agent,
            AgentflowNodeKind.Output,
        ]);
        var manifest = CreateManifest(fixture.Flow.Id);
        var sink = new RecordingSegmentSink();
        var waiting = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            sink,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(DurableExecutionSegmentStatus.WaitingForHuman, waiting.Status);
        Assert.Equal(["input"], sink.Messages.Select(MessageShape));
        var request = Assert.Single(waiting.PendingInteractions);
        Assert.NotNull(waiting.Checkpoint);
        var checkpoint = DurableExecutionJson.DeserializeRequired<DurableAgentflowCheckpoint>(
            DurableExecutionJson.Serialize(waiting.Checkpoint),
            "checkpoint"
        );
        var response = CreateResponse(manifest, request, approved);

        var result = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 1, [response], checkpoint),
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionSegmentStatus.Completed, result.Status);
        Assert.Equal(1, result.SegmentIndex);
        Assert.Equal(approved ? ["input"] : ["input", "human-gate-rejected"], sink.Messages.Select(MessageShape));
        Assert.All(fixture.Agents.CreatedAgents, agent => Assert.True(agent.Disposed));
    }

    [Theory]
    [InlineData("once")]
    [InlineData("always-tool")]
    [InlineData("always-arguments")]
    public async Task ExecuteDurableSegmentAsync_ToolApproval_RestoresApprovalScope(string approvalScope)
    {
        var agents = new List<ApprovalRequestAgent>();
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ =>
            {
                var agent = new ApprovalRequestAgent();
                agents.Add(agent);
                return agent;
            }
        );
        var manifest = CreateManifest(fixture.Flow.Id);
        var sink = new RecordingSegmentSink();
        var waiting = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            sink,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(DurableExecutionSegmentStatus.WaitingForHuman, waiting.Status);
        var request = Assert.Single(waiting.PendingInteractions);
        var response = CreateResponse(manifest, request, true, approvalScope);

        var result = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 1, [response], waiting.Checkpoint),
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionSegmentStatus.Completed, result.Status);
        Assert.Equal(2, agents.Count);
        Assert.Equal(1, agents[0].RunCount);
        Assert.Equal(1, agents[1].RunCount);
        Assert.Equal(
            ["input", approvalScope, approvalScope],
            sink.Messages.Where(message => message.Contents.Count > 0).Select(MessageShape)
        );
        Assert.All(
            sink.Messages.Where(message => message.Contents.Count == 0),
            message => Assert.Equal(AiRole.Assistant, message.Role)
        );
        Assert.DoesNotContain(
            sink.Messages,
            message => MessageShape(message).StartsWith("turn-", StringComparison.Ordinal)
        );
        Assert.Equal("approval-1", request.RequestId);
    }

    [Fact]
    public async Task ExecuteDurableSegmentAsync_ResolvedRequestNotRestored_ReturnsFailureAfterOutput()
    {
        var fixture = CreateCharacterizationFixture([AgentflowNodeKind.Agent, AgentflowNodeKind.Output]);
        var manifest = CreateManifest(fixture.Flow.Id);
        var sink = new RecordingSegmentSink();
        var request = new DurableHumanInteractionSnapshot
        {
            RequestId = "unmatched",
            Kind = "interaction",
            NodeId = "missing",
            Prompt = "unused",
        };

        var result = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 0, [CreateResponse(manifest, request, true)], null),
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionSegmentStatus.Failed, result.Status);
        Assert.Equal("Agentflow did not restore human request 'unmatched'.", result.ErrorMessage);
        Assert.Equal(["input", "done", "done"], sink.Messages.Select(MessageShape));
    }

    [Fact]
    public async Task ExecuteDurableSegmentAsync_MissingCheckpoint_ReturnsFailureAndReleasesAgents()
    {
        var fixture = CreateCharacterizationFixture([AgentflowNodeKind.Agent, AgentflowNodeKind.Output]);
        var manifest = CreateManifest(fixture.Flow.Id);
        var sink = new RecordingSegmentSink();

        var result = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 1, [], null),
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionSegmentStatus.Failed, result.Status);
        Assert.Equal("Agentflow checkpoint could not be found.", result.ErrorMessage);
        Assert.Empty(sink.Messages);
        Assert.All(fixture.Agents.CreatedAgents, agent => Assert.True(agent.Disposed));
    }

    [Fact]
    public async Task ExecuteDurableSegmentAsync_WorkflowFailure_EmitsErrorWithoutTerminalEvent()
    {
        var agent = new ScriptedAgent([], fail: true);
        var fixture = CreateCharacterizationFixture([AgentflowNodeKind.Agent, AgentflowNodeKind.Output], _ => agent);
        var manifest = CreateManifest(fixture.Flow.Id);
        var sink = new RecordingSegmentSink();

        var result = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionSegmentStatus.Failed, result.Status);
        Assert.Equal(["input", "workflow-error"], sink.Messages.Select(MessageShape));
        Assert.StartsWith("Error invoking handler for ", result.ErrorMessage);
        Assert.Equal(1, agent.DisposeCount);
    }

    [Fact]
    public async Task ExecuteDurableSegmentAsync_NoInteractionContext_FailsBeforeResolvingMissingFlow()
    {
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            interactions: false
        );
        var manifest = CreateManifest(Guid.CreateVersion7());

        var result = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            new RecordingSegmentSink(),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionSegmentStatus.Failed, result.Status);
        Assert.Equal("Human interaction context is unavailable.", result.ErrorMessage);
        Assert.Empty(fixture.Agents.CreatedAgents);
    }

    [Fact]
    public async Task ExecuteDurableSegmentAsync_ParallelHumanGates_RestoresEveryResponse()
    {
        var fixture = CreateCharacterizationFixture([
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.Agent,
            AgentflowNodeKind.Output,
        ]);
        fixture.Edges.Remove(fixture.Edges.Queryable.Single(edge => edge.EdgeId == "edge-0"));
        await fixture.Edges.AddAsync(
            new AgentflowEdge
            {
                AgentflowId = fixture.Flow.Id,
                EdgeId = "input-second",
                SourceNodeId = "input",
                TargetNodeId = "node-1",
            }
        );
        await fixture.Edges.AddAsync(
            new AgentflowEdge
            {
                AgentflowId = fixture.Flow.Id,
                EdgeId = "parallel",
                SourceNodeId = "node-0",
                TargetNodeId = "node-2",
            }
        );
        var manifest = CreateManifest(fixture.Flow.Id);
        var sink = new RecordingSegmentSink();
        var waiting = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            sink,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(DurableExecutionSegmentStatus.WaitingForHuman, waiting.Status);
        Assert.Equal(2, waiting.PendingInteractions.Count);
        Assert.Equal(["input"], sink.Messages.Select(MessageShape));

        var result = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(
                manifest.ExecutionId,
                1,
                waiting.PendingInteractions.Select(request => CreateResponse(manifest, request, true)).ToArray(),
                waiting.Checkpoint
            ),
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(DurableExecutionSegmentStatus.Completed, result.Status);
        Assert.Empty(result.PendingInteractions);
        Assert.DoesNotContain(
            sink.Messages,
            message => MessageShape(message).StartsWith("turn-", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execution_ForeignFlow_UsesSameMissingBehavior(bool durable)
    {
        var fixture = CreateCharacterizationFixture([AgentflowNodeKind.Agent, AgentflowNodeKind.Output]);
        fixture.Flow.CreateBy = "someone-else";
        if (durable)
        {
            var manifest = CreateManifest(fixture.Flow.Id);
            var result = await fixture.Service.ExecuteDurableSegmentAsync(
                manifest,
                new(manifest.ExecutionId, 0, [], null),
                new RecordingSegmentSink(),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(DurableExecutionSegmentStatus.Failed, result.Status);
            Assert.Equal("Agentflow could not be found.", result.ErrorMessage);
        }
        else
        {
            Assert.Null(
                await fixture.Service.ExecuteAsync(
                    fixture.Flow.Id,
                    Guid.CreateVersion7(),
                    "input",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Null(await fixture.Service.GetMermaidAsync(fixture.Flow.Id, TestContext.Current.CancellationToken));
        }
        Assert.Empty(fixture.Agents.CreatedAgents);
    }

    private static CharacterizationFixture CreateCharacterizationFixture(
        AgentflowNodeKind[] kinds,
        Func<Guid, AIAgent?>? agentFactory = null,
        bool interactions = true,
        AgentflowCheckpointStore? checkpointStore = null,
        AgentSessionStateStore? sessionStateStore = null,
        IProviderSessionState? providerSessionState = null,
        IProjectDefaultResolver? projectDefaults = null,
        IProjectRuntimeFacade? projectRuntimeFacade = null,
        IRuntimeTurnContextAccessor? turnContextAccessor = null
    )
    {
        var flow = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "characterization",
            CreateBy = "tester",
        };
        var agentId = Guid.CreateVersion7();
        var nodes = kinds
            .Select(
                (kind, index) =>
                    new AgentflowNode
                    {
                        AgentflowId = flow.Id,
                        NodeId = $"node-{index}",
                        Name = $"Node {index}",
                        Kind = kind,
                        RelateId = kind == AgentflowNodeKind.Agent ? agentId : null,
                    }
            )
            .ToArray();
        nodes =
        [
            .. nodes,
            new AgentflowNode
            {
                AgentflowId = flow.Id,
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
            },
        ];
        var edges = Enumerable
            .Range(0, kinds.Length - 1)
            .Select(index => new AgentflowEdge
            {
                AgentflowId = flow.Id,
                EdgeId = $"edge-{index}",
                SourceNodeId = nodes[index].NodeId,
                TargetNodeId = nodes[index + 1].NodeId,
            })
            .ToList();
        edges.Add(
            new AgentflowEdge
            {
                AgentflowId = flow.Id,
                EdgeId = "input-first",
                SourceNodeId = "input",
                TargetNodeId = "node-0",
            }
        );
        var agents =
            agentFactory == null ? new StubAgentRuntimeService(agentId) : new StubAgentRuntimeService(agentFactory);
        var edgeRepository = new TestRepository<AgentflowEdge>(edges, edge => edge.EdgeId);
        var nodeRepository = new TestRepository<AgentflowNode>(nodes, node => node.NodeId);
        var service = CreateRuntimeService(
            NullLogger<AgentflowRuntimeService>.Instance,
            new TestRepository<Agentflow>([flow], item => item.Id),
            nodeRepository,
            edgeRepository,
            agents,
            providerSessionState ?? new StubProviderSessionState(),
            new RecordingSummaryService(),
            sessionStateStore: sessionStateStore,
            humanInteractionContextAccessor: interactions ? new HumanInteractionContextAccessor() : null,
            checkpointStore: checkpointStore,
            projectDefaults: projectDefaults,
            projectRuntimeFacade: projectRuntimeFacade,
            turnContextAccessor: turnContextAccessor
        );
        return new CharacterizationFixture(flow, nodes, nodeRepository, edgeRepository, agents, service);
    }

    private sealed record CharacterizationFixture(
        Agentflow Flow,
        AgentflowNode[] Nodes,
        TestRepository<AgentflowNode> NodeRepository,
        TestRepository<AgentflowEdge> Edges,
        StubAgentRuntimeService Agents,
        AgentflowRuntimeService Service
    );

    private static DurableExecutionManifest CreateManifest(Guid flowId) =>
        new()
        {
            ExecutionId = Guid.CreateVersion7(),
            UserId = "tester",
            AgentId = flowId,
            AgentType = AgentRuntimeType.Agentflow,
            Input = new AgwUserInput { Author = "user", Contents = [new AgwTextContent { Content = "input" }] },
            Task = new DurableProjectTaskSnapshot
            {
                TaskId = Guid.CreateVersion7(),
                ProjectId = Guid.CreateVersion7(),
                ProjectConversationId = Guid.CreateVersion7(),
                ContextId = "context",
            },
            Settings = new DurableExecutionSettings { EnvironmentVariables = [], Resume = false },
        };

    private static DurableResolvedInteraction CreateResponse(
        DurableExecutionManifest manifest,
        DurableHumanInteractionSnapshot request,
        bool approved,
        string approvalScope = "once"
    ) =>
        new(
            request,
            new DurableHumanResponseEnvelope
            {
                ExecutionId = manifest.ExecutionId,
                RequestId = request.RequestId,
                Approved = approved,
                ResponseText = " accepted ",
                ApprovalScope = approvalScope,
            }
        );

    private static async Task<List<AgwMessage>> CollectAsync(IAsyncEnumerable<AgwMessage> stream)
    {
        var messages = new List<AgwMessage>();
        await foreach (var message in stream)
            messages.Add(message);
        return messages;
    }

    private static string MessageShape(AgwMessage message) =>
        message.AdditionalProperties?.TryGetValue("type", out var type) == true
            ? type?.ToString() ?? ""
            : string.Concat(message.Contents.OfType<AgwTextContent>().Select(content => content.Content));

    private sealed class RecordingSegmentSink : IExecutionMessageSink
    {
        public List<AgwMessage> Messages { get; } = [];

        public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(message);
        }
    }

    private sealed class FixedApprovalHandler : IHumanGateApprovalHandler
    {
        private readonly bool _approved;
        private readonly string? _text;

        public FixedApprovalHandler(bool approved, string? text = null)
        {
            _approved = approved;
            _text = text;
        }

        public async ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
            HumanGateApprovalRequest request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(20, cancellationToken);
            return new HumanGateApprovalDecision(request.RequestId, _approved, _text);
        }
    }

    private sealed class ScriptedAgent : AIAgent, IAsyncDisposable
    {
        private readonly IReadOnlyList<string> _fragments;
        private readonly bool _fail;
        private readonly bool _failOnDispose;

        public ScriptedAgent(IReadOnlyList<string> fragments, bool fail = false, bool failOnDispose = false)
        {
            _fragments = fragments;
            _fail = fail;
            _failOnDispose = failOnDispose;
        }

        protected override string IdCore => "scripted";
        public override string Name => "scripted";
        public int DisposeCount { get; private set; }
        public List<List<ChatMessage>> Inputs { get; } = [];

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ScriptedSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new ScriptedSession());

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
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            Inputs.Add(messages.ToList());
            await Task.Yield();
            if (_fail)
                throw new InvalidOperationException("scripted failure");
            foreach (var fragment in _fragments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AgentResponseUpdate(ChatRole.Assistant, fragment)
                {
                    MessageId = "scripted-message",
                    AuthorName = Name,
                };
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            DisposeCount++;
            if (_failOnDispose)
                throw new InvalidOperationException("cleanup failed");
        }

        private sealed class ScriptedSession : AgentSession;
    }
}
