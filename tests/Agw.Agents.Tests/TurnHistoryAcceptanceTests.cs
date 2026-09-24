using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Application.History;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

/// <summary>
/// 历史路径的验收用例：Step 序号与存档边界、等待审批的结局、消息行幂等与各 Engine 读取一致。
/// Acceptance cases of the history path: Step indexes and checkpoint boundaries, the awaiting-approval outcome, message-row idempotency and consistent reads across Engines.
/// </summary>
public sealed class TurnHistoryAcceptanceTests : IDisposable
{
    private readonly IDisposable _userScope = HistoryTestFixture.EnterUser();

    public void Dispose() => _userScope.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunAsync_ThreeSteps_StampsStepIndexesAndSavesStepCompletedBeforeEachNextRequest()
    {
        // Arrange: 第二个 Step 含一个被拒绝与一个执行失败的工具调用。
        // Arrange: the second Step contains one rejected and one failing tool call.
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var checkpoints = new InMemoryTurnCheckpointStore();
        var model = new ThreeStepChatClient(fixture, checkpoints);
        var agent = CreateAgent(fixture, model);
        var runtime = await CreateRuntimeAsync(fixture, agent);
        TurnOutcome? outcome;

        // Act
        await using (var turn = fixture.BeginTurn(checkpoints: checkpoints))
        {
            turn.Scope.BindInteractions(
                new PendingInteractionHandler(
                    HumanInteractionPolicy.Allow,
                    new InteractionRequestRegistry(),
                    allowInteraction: false
                ),
                channel: null,
                requests: null
            );
            await RunTurnAsync(fixture, turn, runtime);
            outcome = turn.Scope.Outcome;
        }

        // Assert
        Assert.Equal(3, model.Requests);
        Assert.Equal(3, outcome!.StepCount);
        var beforeSecond = Assert.Single(model.CheckpointsBeforeRequest[1]);
        Assert.Equal((TurnCheckpointKind.StepCompleted, 1), (beforeSecond.Kind, beforeSecond.StepIndex));
        var beforeThird = model.CheckpointsBeforeRequest[2][^1];
        Assert.Equal((TurnCheckpointKind.StepCompleted, 2), (beforeThird.Kind, beforeThird.StepIndex));
        var committedResults = model
            .CommittedBeforeThirdRequest.Where(row =>
                row.Message.Contents.OfType<FunctionResultContent>().Any(result => result.CallId != "call-1")
            )
            .ToList();
        Assert.Equal(
            ["call-2", "call-3"],
            committedResults
                .SelectMany(row => row.Message.Contents.OfType<FunctionResultContent>())
                .Select(result => result.CallId)
                .Order()
        );
        Assert.All(committedResults, row => Assert.True(row.Sequence <= beforeThird.HistoryThroughSequence));
        var completed = checkpoints.Saved[^1];
        Assert.Equal((TurnCheckpointKind.TurnCompleted, 3), (completed.Kind, completed.StepIndex));

        var messages = await fixture.ReadMessagesAsync();
        Assert.Equal(0, StepIndex(Assert.Single(messages, message => message.Text == "question")));
        Assert.Equal(1, StepIndex(Assert.Single(messages, message => HasCall(message, "call-1"))));
        Assert.Equal(1, StepIndex(Assert.Single(messages, message => HasResult(message, "call-1"))));
        Assert.Equal(2, StepIndex(Assert.Single(messages, message => HasCall(message, "call-2"))));
        Assert.All(
            messages.Where(message => HasResult(message, "call-2") || HasResult(message, "call-3")),
            message => Assert.Equal(2, StepIndex(message))
        );
        Assert.Equal(3, StepIndex(Assert.Single(messages, message => message.Text == "done")));
    }

    [Fact]
    public async Task RunAsync_ApprovalWaits_SavesAwaitingInputWithoutTurnCompleted()
    {
        // Arrange
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var checkpoints = new InMemoryTurnCheckpointStore();
        var model = new ThreeStepChatClient(fixture, checkpoints) { ApprovalOnly = true };
        var agent = CreateAgent(fixture, model);
        var runtime = await CreateRuntimeAsync(fixture, agent);
        var interactions = new DurablePendingInteractionSet(
            null,
            DurableExecutionCoordinator.RestoreInteractions(
                new DurableExecutionSegmentInput(Guid.CreateVersion7(), 0, [], null)
            )
        );
        TurnOutcome? outcome;

        // Act
        await using (var turn = fixture.BeginTurn(checkpoints: checkpoints, interactions: interactions))
        {
            turn.Scope.BindInteractions(
                new PendingInteractionHandler(HumanInteractionPolicy.Allow, new InteractionRequestRegistry()),
                channel: null,
                requests: null
            );
            await RunTurnAsync(fixture, turn, runtime);
            outcome = turn.Scope.Outcome;
        }

        // Assert
        Assert.Equal(TurnOutcomeStatus.WaitingForHuman, outcome!.Status);
        var saved = Assert.Single(checkpoints.Saved);
        Assert.Equal(TurnCheckpointKind.AwaitingInput, saved.Kind);
        Assert.Equal(1, saved.StepIndex);
        Assert.Equal("call-2", Assert.Single(saved.PendingInteractions).CallId);
        Assert.DoesNotContain(checkpoints.Saved, checkpoint => checkpoint.Kind == TurnCheckpointKind.TurnCompleted);
        Assert.Contains(await fixture.ReadMessagesAsync(), message => HasCall(message, "call-2"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendAsync_SameMessageIdTwice_KeepsOneRowAndSequence(bool insideTurn)
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var writer = fixture.CreateWriter();
        var id = Guid.CreateVersion7().ToString("D");
        await writer.AppendAsync(
            fixture.ProjectId,
            HistoryTestFixture.ContextId,
            [new ChatMessage(ChatRole.User, "before")],
            Token
        );

        if (insideTurn)
        {
            await using (fixture.BeginTurn())
            {
                await writer.AppendAsync(
                    fixture.ProjectId,
                    HistoryTestFixture.ContextId,
                    [new ChatMessage(ChatRole.Assistant, "answer") { MessageId = id }],
                    Token
                );
                await writer.AppendAsync(
                    fixture.ProjectId,
                    HistoryTestFixture.ContextId,
                    [new ChatMessage(ChatRole.Assistant, "answer") { MessageId = id }],
                    Token
                );
            }
        }
        else
        {
            for (var index = 0; index < 2; index++)
                await writer.AppendAsync(
                    fixture.ProjectId,
                    HistoryTestFixture.ContextId,
                    [new ChatMessage(ChatRole.Assistant, "answer") { MessageId = id }],
                    Token
                );
        }

        var rows = await fixture.ReadAsync();
        Assert.Equal(2, rows.Count);
        var row = Assert.Single(rows, row => row.Id == Guid.Parse(id));
        Assert.Equal(1, row.ConversationSequence);
        Assert.Equal("answer", row.GetText());
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_SystemAndExternalEngines_ReadTheSameSharedHistory()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var displayOnly = new ChatMessage(ChatRole.System, "progress");
        ConversationHistoryMetadata.ExcludeFromModelHistory(displayOnly);
        await fixture
            .CreateWriter()
            .AppendAsync(
                fixture.ProjectId,
                HistoryTestFixture.ContextId,
                [
                    new ChatMessage(ChatRole.User, "question"),
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())]
                    ),
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "found")]),
                    new ChatMessage(ChatRole.Assistant, "answer"),
                    displayOnly,
                    new ChatMessage(ChatRole.Assistant, "summary")
                    {
                        AdditionalProperties = new() { ["type"] = "result" },
                    },
                ],
                Token
            );

        var reads = new List<string[]>();
        foreach (var engine in new[] { EngineKind.Maf, EngineKind.ClaudeCode, EngineKind.Codex, EngineKind.Pi })
        {
            var history = fixture.CreateProvider(engine);
            var agent = new HistoryNotifyingAgent(history);
            var session = await fixture.CreateSessionAsync(agent);
            var messages = await HistoryTestFixture.ReplayAsync(history, agent, session);
            reads.Add(messages.Select(Describe).ToArray());
        }

        Assert.Equal(["user:question", "assistant:call:call-1", "tool:result:call-1", "assistant:answer"], reads[0]);
        Assert.All(reads, read => Assert.Equal(reads[0], read));
    }

    private static string Describe(ChatMessage message) =>
        message.Contents.FirstOrDefault() switch
        {
            FunctionCallContent call => $"{message.Role}:call:{call.CallId}",
            FunctionResultContent result => $"{message.Role}:result:{result.CallId}",
            _ => $"{message.Role}:{message.Text}",
        };

    private static int? StepIndex(ChatMessage message) =>
        message.AdditionalProperties?.GetValueOrDefault("stepIndex") is { } value
            ? Convert.ToInt32(value.ToString())
            : null;

    private static bool HasCall(ChatMessage message, string callId) =>
        message.Contents.OfType<FunctionCallContent>().Any(call => call.CallId == callId);

    private static bool HasResult(ChatMessage message, string callId) =>
        message.Contents.OfType<FunctionResultContent>().Any(result => result.CallId == callId);

    private static AIAgent CreateAgent(HistoryTestFixture fixture, IChatClient model)
    {
        var history = fixture.CreateProvider();
        var capabilities = new AgentCapabilityComposition(
            tools:
            [
                AIFunctionFactory.Create(() => "found", "lookup"),
                new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => "guarded result", "guarded")),
                AIFunctionFactory.Create((Func<string>)(() => throw new InvalidOperationException("broken")), "broken"),
            ],
            pluginSkills: [],
            warnings: [],
            contextProviders: [],
            loopEvaluators: [],
            autoApprovalRules: [],
            planModeAllowedToolNames: new HashSet<string>(),
            toolWarnings: [],
            toolInvocationWarnings: new Dictionary<string, string>(),
            lease: new AgentResourceLease()
        );
        var definition = new ResolvedAgentDefinition
        {
            Id = "step-agent",
            Name = "Step agent",
            ModelId = "test-model",
            OpenTelemetrySourceName = "test-source",
            ChatHistoryProvider = history,
        };
        return HistoryTestFixture.Record(
            model.AsAgwAgent(definition, capabilities, NullLoggerFactory.Instance, fixture.Services),
            history
        );
    }

    private static async Task<AgentRuntime> CreateRuntimeAsync(HistoryTestFixture fixture, AIAgent agent) =>
        new(
            NullLogger.Instance,
            agent,
            await fixture.CreateSessionAsync(agent),
            fixture.ProjectId,
            HistoryTestFixture.ContextId,
            sessionStateScope: null
        );

    private static async Task RunTurnAsync(HistoryTestFixture fixture, HistoryTurn turn, AgentRuntime runtime)
    {
        var executor = new AgentTurnExecutor(fixture.Store, null, null, NullLogger<AgentTurnExecutor>.Instance);
        var input = new TurnInput(new AgwUserInput { Contents = [new AgwTextContent { Content = "question" }] });
        await foreach (var _ in turn.Scope.RunStreaming(executor.RunAsync(turn.Scope, runtime, input, Token))) { }
    }

    /// <summary>
    /// 三次模型调用：查询工具、一个需要审批与一个会失败的工具、最终回答；每次调用前记录已保存的存档与已提交的行。
    /// Three model calls: a lookup tool, one tool that needs approval plus one that fails, then the final answer; before each call it records saved checkpoints and committed rows.
    /// </summary>
    private sealed class ThreeStepChatClient : IChatClient
    {
        private readonly HistoryTestFixture _fixture;
        private readonly InMemoryTurnCheckpointStore _checkpoints;

        public ThreeStepChatClient(HistoryTestFixture fixture, InMemoryTurnCheckpointStore checkpoints)
        {
            _fixture = fixture;
            _checkpoints = checkpoints;
        }

        /// <summary>
        /// 为真时第一次调用直接请求需要审批的工具。
        /// When true the first call directly requests the tool that needs approval.
        /// </summary>
        public bool ApprovalOnly { get; init; }

        public int Requests { get; private set; }

        public List<IReadOnlyList<AgentTurnCheckpoint>> CheckpointsBeforeRequest { get; } = [];

        public List<(long? Sequence, ChatMessage Message)> CommittedBeforeThirdRequest { get; private set; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            CheckpointsBeforeRequest.Add(_checkpoints.Saved.ToList());
            var request = ++Requests;
            if (request == 3)
                CommittedBeforeThirdRequest = (await _fixture.ReadAsync())
                    .Select(row => (row.ConversationSequence, row.ToChatMessage()!))
                    .ToList();
            List<AIContent> contents = (ApprovalOnly ? request + 1 : request) switch
            {
                1 => [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())],
                2 =>
                [
                    new FunctionCallContent("call-2", "guarded", new Dictionary<string, object?>()),
                    new FunctionCallContent("call-3", "broken", new Dictionary<string, object?>()),
                ],
                _ => [new TextContent("done")],
            };
            var response = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, contents) { MessageId = $"response-{request}" }
            );
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
