using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Projects.Application.History;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class HistoryRecordingAgentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_WithMemory_PersistsOriginalOnceAndForwardsComposite(bool streaming)
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.Pi);
        var innerAgent = new HistoryNotifyingAgent(history);
        var agent = HistoryTestFixture.Record(innerAgent, history, "private memory");
        var session = await fixture.CreateSessionAsync(agent);
        var request = new ChatMessage(ChatRole.User, "current request") { MessageId = "request-1" };

        if (streaming)
            await DrainAsync(
                agent.RunStreamingAsync([request], session, cancellationToken: TestContext.Current.CancellationToken)
            );
        else
            await agent.RunAsync([request], session, cancellationToken: TestContext.Current.CancellationToken);

        var forwarded = Assert.Single(innerAgent.RequestMessages);
        Assert.Equal("private memory\n\n## Current Request\n\ncurrent request", forwarded.Text);
        Assert.True(ConversationHistoryMetadata.IsPersistenceExcluded(forwarded));
        Assert.False(ConversationHistoryMetadata.IsPersistenceExcluded(request));
        var records = await fixture.ReadAsync();
        Assert.Equal(["current request", "answer"], records.Select(record => record.GetText()));
        Assert.Equal(
            request.MessageId,
            records[0].ToChatMessage()!.AdditionalProperties!["sourceMessageId"]?.ToString()
        );
        Assert.Equal(records[0].TaskId, records[1].TaskId);
        Assert.DoesNotContain("current request", session.StateBag.Serialize().GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_InnerDoesNotNotifyHistory_PersistsRequestAndResponse()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.Pi);
        var innerAgent = new HistoryNotifyingAgent(historyProvider: null);
        var agent = HistoryTestFixture.Record(innerAgent, history);
        var session = await fixture.CreateSessionAsync(agent);

        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "current request")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(ConversationHistoryMetadata.IsPersistenceExcluded(Assert.Single(innerAgent.RequestMessages)));
        Assert.Equal(["current request", "answer"], (await fixture.ReadAsync()).Select(record => record.GetText()));
    }

    [Fact]
    public async Task InvokingAsync_StagedInputAndTransientCopy_PersistsOriginalOnly()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.Pi);
        var agent = new HistoryNotifyingAgent(historyProvider: null);
        var session = await fixture.CreateSessionAsync(agent);
        var recording = history.Begin(agent, session);
        recording.Stage([new ChatMessage(ChatRole.User, "original")]);
        var transient = new ChatMessage(ChatRole.User, "transient");
        ConversationHistoryMetadata.ExcludeFromPersistence(transient);

        await history.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(agent, session, [transient]),
            TestContext.Current.CancellationToken
        );
        await history.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(
                agent,
                session,
                [transient],
                [new ChatMessage(ChatRole.Assistant, "answer")]
            ),
            TestContext.Current.CancellationToken
        );
        await recording.FinishAsync(completed: true, TestContext.Current.CancellationToken);
        history.End(session);

        Assert.Equal(["original", "answer"], (await fixture.ReadAsync()).Select(record => record.GetText()));
    }

    [Fact]
    public async Task RunAsync_InnerReportsFailure_PersistsOriginalAndPreservesInvokeException()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.Pi);
        var failure = new InvalidOperationException("SDK failure");
        var agent = HistoryTestFixture.Record(new HistoryNotifyingAgent(history, failure), history, "private memory");
        var session = await fixture.CreateSessionAsync(agent);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.RunAsync(
                [new ChatMessage(ChatRole.User, "current request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Same(failure, thrown);
        Assert.Equal("current request", Assert.Single(await fixture.ReadAsync()).GetText());
    }

    [Fact]
    public async Task RunAsync_SystemChatClientPipeline_PreservesTransientMarkerAndPersistsOnlyOriginalRequest()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider();
        var chatClient = new CapturingChatClient();
        await using var capabilities = CreateCapabilities();
        var innerAgent = chatClient.AsAgwAgent(
            CreateDefinition(history),
            capabilities,
            NullLoggerFactory.Instance,
            fixture.Services
        );
        var agent = HistoryTestFixture.Record(innerAgent, history, "private memory");
        var session = await fixture.CreateSessionAsync(agent);

        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "current request")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Single(chatClient.Requests);
        Assert.Equal(["current request", "answer"], (await fixture.ReadAsync()).Select(record => record.GetText()));
    }

    [Theory]
    [InlineData("once")]
    [InlineData("always-tool")]
    [InlineData("always-arguments")]
    public async Task RunAsync_FunctionApprovalResponse_PersistsDisplayOnlyResponseAndForwardsOriginal(
        string approvalScope
    )
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.Pi);
        var innerAgent = new HistoryNotifyingAgent(history);
        var agent = HistoryTestFixture.Record(innerAgent, history);
        var session = await fixture.CreateSessionAsync(agent);
        var approvalRequest = new ToolApprovalRequestContent(
            "approval-1",
            new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?> { ["path"] = "README.md" })
        );
        AIContent approval = approvalScope switch
        {
            "once" => approvalRequest.CreateResponse(approved: true),
            "always-tool" => approvalRequest.CreateAlwaysApproveToolResponse(),
            _ => approvalRequest.CreateAlwaysApproveToolWithArgumentsResponse(),
        };
        var persistedApproval = approval is AlwaysApproveToolApprovalResponseContent alwaysApproval
            ? alwaysApproval.InnerResponse
            : (ToolApprovalResponseContent)approval;
        persistedApproval.AdditionalProperties = new() { ["approvalScope"] = approvalScope };

        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, [approval])],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Same(approval, Assert.Single(Assert.Single(innerAgent.RequestMessages).Contents));
        var message = (await fixture.ReadAsync())[0].ToChatMessage()!;
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(message));
        var stored = Assert.IsType<ToolApprovalResponseContent>(Assert.Single(message.Contents));
        Assert.True(stored.Approved);
        Assert.Equal("approval-1", stored.RequestId);
        Assert.Equal(approvalScope, stored.AdditionalProperties!["approvalScope"]?.ToString());
    }

    [Fact]
    public async Task RunStreamingAsync_GetAsyncEnumeratorThrows_PersistsOriginalAndPreservesFailure()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.Pi);
        var failure = new InvalidOperationException("enumerator failure");
        var agent = HistoryTestFixture.Record(new GetEnumeratorThrowingAgent(failure), history);
        var session = await fixture.CreateSessionAsync(agent);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DrainAsync(
                agent.RunStreamingAsync(
                    [new ChatMessage(ChatRole.User, "current request")],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
        );

        Assert.Same(failure, thrown);
        Assert.Equal("current request", Assert.Single(await fixture.ReadAsync()).GetText());
    }

    [Fact]
    public async Task RunStreamingAsync_SdkWritesCompleteMessageBeforeDelta_PersistsOneRow()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.Pi);
        var agent = HistoryTestFixture.Record(new HistoryNotifyingAgent(history, notifyBeforeYield: true), history);
        var session = await fixture.CreateSessionAsync(agent);

        var updates = new List<AgentResponseUpdate>();
        await foreach (
            var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "current request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            updates.Add(update);

        var records = await fixture.ReadAsync();
        Assert.Equal(["current request", "answer"], records.Select(record => record.GetText()));
        Assert.Equal(records[1].Id.ToString("D"), Assert.Single(updates).MessageId);
    }

    [Fact]
    public async Task StreamingHistory_ThinkingToolLoop_ReturnsReasoningInContinuationRequest()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        await using var model = new OpenAiReasoningChatClientTests.ClientFixture("https://gateway.example.test/v1");
        model.Handler.CompleteAfterTools = true;
        await using var capabilities = CreateCapabilities([AIFunctionFactory.Create(() => "tool result", "lookup")]);
        var history = fixture.CreateProvider();
        var definition = new ResolvedAgentDefinition
        {
            Id = "thinking-agent",
            Name = "Thinking agent",
            ModelId = "thinking-model",
            OpenTelemetrySourceName = "test-source",
            ChatHistoryProvider = history,
            CompactionProvider = new CompactionProvider(new ContextWindowCompactionStrategy(100_000, 10_000)),
        };
        var agent = HistoryTestFixture.Record(
            model.Client.AsAgwAgent(definition, capabilities, NullLoggerFactory.Instance, fixture.Services),
            history
        );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var session = await fixture.CreateSessionAsync(agent);

        await using (fixture.BeginTurn())
            await foreach (
                var _ in agent.RunStreamingAsync(
                    [new ChatMessage(ChatRole.User, "question")],
                    session,
                    cancellationToken: timeout.Token
                )
            ) { }

        Assert.Equal(2, model.Handler.Requests.Count);
        var toolCall = Assert.Single(
            model.Handler.Requests[1].GetProperty("messages").EnumerateArray(),
            message => message.TryGetProperty("tool_calls", out _)
        );
        Assert.True(toolCall.TryGetProperty("reasoning_content", out var reasoning));
        Assert.Equal("original reasoning", reasoning.GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamingHistory_LongModelCall_PersistsInputAndProgressBeforeCompletionWithoutDuplicates(
        bool messageIds
    )
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        await using var capabilities = CreateCapabilities();
        var model = new PausedChatClient { IncludeMessageIds = messageIds };
        var history = fixture.CreateProvider();
        var agent = HistoryTestFixture.Record(
            model.AsAgwAgent(CreateDefinition(history), capabilities, NullLoggerFactory.Instance, fixture.Services),
            history,
            "private memory"
        );
        var session = await fixture.CreateSessionAsync(agent);
        List<Agw.Shared.Data.Entities.Projects.ProjectConversationChatHistory> partial;
        DateTimeOffset? messageCreatedAt;
        await using (fixture.BeginTurn())
        {
            await using var enumerator = agent
                .RunStreamingAsync(
                    [new ChatMessage(ChatRole.User, "question")],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);

            var first = enumerator.MoveNextAsync().AsTask();
            await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Single(model.Input, message => message.Role == ChatRole.User);
            await fixture.AdvanceFlushTimerAsync();
            await fixture.WaitForCommitAsync();
            Assert.Equal("question", Assert.Single(await fixture.ReadAsync()).GetText());
            Assert.False(first.IsCompleted);

            model.Emit("first ");
            Assert.True(await first);
            messageCreatedAt = enumerator.Current.ToAiMessage()!.CreatedAt;
            Assert.NotNull(messageCreatedAt);
            var second = enumerator.MoveNextAsync().AsTask();
            await fixture.AdvanceFlushTimerAsync();
            await fixture.WaitForCommitAsync();
            partial = await fixture.ReadAsync();
            Assert.Equal(["question", "first "], partial.Select(row => row.GetText()));
            Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(partial[1].ToChatMessage()!));
            Assert.Equal(partial[0].TaskId, partial[1].TaskId);
            Assert.Equal(messageCreatedAt, partial[1].ToChatMessage()!.ToAiMessage()!.CreatedAt);

            model.Emit("second");
            Assert.True(await second);
            Assert.Equal(messageCreatedAt, enumerator.Current.ToAiMessage()!.CreatedAt);
            var end = enumerator.MoveNextAsync().AsTask();
            await fixture.AdvanceFlushTimerAsync();
            await fixture.WaitForCommitAsync();
            var later = await fixture.ReadAsync();
            Assert.Equal(partial[1].Id, later[1].Id);
            Assert.Equal("first second", later[1].GetText());
            model.Finish();
            while (await end)
                end = enumerator.MoveNextAsync().AsTask();
        }

        var completed = await fixture.ReadAsync();
        Assert.Equal(2, completed.Count);
        Assert.Equal(partial[1].Id, completed[1].Id);
        Assert.Equal(partial[1].ConversationSequence, completed[1].ConversationSequence);
        Assert.Equal(partial[1].CreateTime, completed[1].CreateTime);
        Assert.True(completed[1].UpdateTime > partial[1].UpdateTime);
        Assert.Equal("first second", completed[1].GetText());
        Assert.Equal(messageCreatedAt, completed[1].ToChatMessage()!.ToAiMessage()!.CreatedAt);
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(completed[1].ToChatMessage()!));
        var replay = await HistoryTestFixture.ReplayAsync(history, agent, session);
        Assert.Equal(["question", "first second"], replay.Select(message => message.Text));
        Assert.DoesNotContain(
            completed,
            row => row.ConversationPayload!.Contains("private memory", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.TurnEnd)]
    [InlineData(ConversationHistoryWriteMode.Interval)]
    public async Task StreamingHistory_EarlyDisposal_PreservesPartialResponseAndExcludesItFromModelHistory(
        ConversationHistoryWriteMode mode
    )
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(mode);
        await using var capabilities = CreateCapabilities();
        var model = new PausedChatClient();
        var history = fixture.CreateProvider();
        var agent = HistoryTestFixture.Record(
            model.AsAgwAgent(CreateDefinition(history), capabilities, NullLoggerFactory.Instance, fixture.Services),
            history
        );
        var session = await fixture.CreateSessionAsync(agent);

        await using (fixture.BeginTurn())
        await using (
            var enumerator = agent
                .RunStreamingAsync(
                    [new ChatMessage(ChatRole.User, "question")],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .GetAsyncEnumerator(TestContext.Current.CancellationToken)
        )
        {
            model.Emit("interrupted answer");
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Empty(await fixture.ReadAsync());
            if (mode == ConversationHistoryWriteMode.TurnEnd)
            {
                fixture.Clock.Advance(TimeSpan.FromMinutes(1));
                Assert.Empty(await fixture.ReadAsync());
            }
        }

        Assert.Equal(["question", "interrupted answer"], (await fixture.ReadAsync()).Select(row => row.GetText()));
        var replay = await HistoryTestFixture.ReplayAsync(history, agent, session);
        Assert.Equal("question", Assert.Single(replay).Text);
    }

    [Fact]
    public async Task StreamingHistory_ToolLoop_PreservesInputOrderingAndModelContext()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        await using var capabilities = CreateCapabilities([AIFunctionFactory.Create(() => "tool result", "lookup")]);
        var model = new PausedChatClient { CallToolFirst = true };
        var history = fixture.CreateProvider();
        var agent = HistoryTestFixture.Record(
            model.AsAgwAgent(CreateDefinition(history), capabilities, NullLoggerFactory.Instance, fixture.Services),
            history
        );
        var session = await fixture.CreateSessionAsync(agent);

        await using (fixture.BeginTurn())
        {
            var draining = DrainAsync(
                agent.RunStreamingAsync(
                    [new ChatMessage(ChatRole.User, "question")],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            );
            await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(
                [ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                model.Input.Select(message => message.Role)
            );
            Assert.Single(model.Input.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
            Assert.Single(model.Input.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
            await fixture.AdvanceFlushTimerAsync();
            await fixture.WaitForCommitAsync();
            Assert.Equal(
                [ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                (await fixture.ReadAsync()).Select(row => row.ToChatMessage()!.Role)
            );
            model.Emit("answer after tool");
            model.Finish();
            await draining.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        var replay = await HistoryTestFixture.ReplayAsync(history, agent, session);
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant],
            replay.Select(message => message.Role)
        );
        Assert.Equal(4, (await fixture.ReadAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingHistory_ModelFailsOrCancels_PreservesPartialOutputAndOriginalFailure(bool cancel)
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var capabilities = CreateCapabilities();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var model = new PausedChatClient();
        var history = fixture.CreateProvider();
        var agent = HistoryTestFixture.Record(
            model.AsAgwAgent(CreateDefinition(history), capabilities, NullLoggerFactory.Instance, fixture.Services),
            history
        );
        var session = await fixture.CreateSessionAsync(agent);

        await using (fixture.BeginTurn())
        await using (
            var enumerator = agent
                .RunStreamingAsync(
                    [new ChatMessage(ChatRole.User, "question")],
                    session,
                    cancellationToken: cancellation.Token
                )
                .GetAsyncEnumerator(cancellation.Token)
        )
        {
            model.Emit("partial");
            Assert.True(await enumerator.MoveNextAsync());
            var next = enumerator.MoveNextAsync().AsTask();
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
            }
            else
            {
                var failure = new InvalidOperationException("model failed");
                model.Finish(failure);
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => next));
            }
        }

        var rows = await fixture.ReadAsync();
        Assert.Equal(["question", "partial"], rows.Select(row => row.GetText()));
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(rows[1].ToChatMessage()!));
    }

    [Fact]
    public async Task StreamingHistory_ToolLoopDisposedDuringSecondCall_DoesNotDuplicateToolResult()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var capabilities = CreateCapabilities([AIFunctionFactory.Create(() => "tool result", "lookup")]);
        var model = new PausedChatClient { CallToolFirst = true };
        var history = fixture.CreateProvider();
        var agent = HistoryTestFixture.Record(
            model.AsAgwAgent(CreateDefinition(history), capabilities, NullLoggerFactory.Instance, fixture.Services),
            history
        );
        var session = await fixture.CreateSessionAsync(agent);
        model.Emit("partial second reply");

        await using (fixture.BeginTurn())
        await using (
            var enumerator = agent
                .RunStreamingAsync(
                    [new ChatMessage(ChatRole.User, "question")],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .GetAsyncEnumerator(TestContext.Current.CancellationToken)
        )
        {
            while (await enumerator.MoveNextAsync())
                if (enumerator.Current.Text == "partial second reply")
                    break;
        }

        var records = await fixture.ReadAsync();
        Assert.Single(records.SelectMany(record => record.ToChatMessage()!.Contents).OfType<FunctionResultContent>());
        Assert.Equal(4, records.Count);
    }

    private static async Task DrainAsync(IAsyncEnumerable<AgentResponseUpdate> execution)
    {
        await foreach (var _ in execution.WithCancellation(TestContext.Current.CancellationToken)) { }
    }

    private static ResolvedAgentDefinition CreateDefinition(ChatHistoryProvider historyProvider) =>
        new()
        {
            Id = "system-agent",
            Name = "System agent",
            ModelId = "test-model",
            OpenTelemetrySourceName = "test-source",
            ChatHistoryProvider = historyProvider,
        };

    private static AgentCapabilityComposition CreateCapabilities(IReadOnlyList<AITool>? tools = null) =>
        new(
            tools: tools ?? [],
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

    private sealed class GetEnumeratorThrowingAgent : AIAgent
    {
        private readonly Exception _exception;

        public GetEnumeratorThrowingAgent(Exception exception)
        {
            _exception = exception;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new TestSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => Task.FromException<AgentResponse>(_exception);

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => new GetEnumeratorThrowingAsyncEnumerable(_exception);

        private sealed class TestSession : AgentSession;
    }

    private sealed class GetEnumeratorThrowingAsyncEnumerable : IAsyncEnumerable<AgentResponseUpdate>
    {
        private readonly Exception _exception;

        public GetEnumeratorThrowingAsyncEnumerable(Exception exception)
        {
            _exception = exception;
        }

        public IAsyncEnumerator<AgentResponseUpdate> GetAsyncEnumerator(
            CancellationToken cancellationToken = default
        ) => throw _exception;
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(messages.ToList());
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Requests.Add(messages.ToList());
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "answer");
        }
    }

    private sealed class PausedChatClient : IChatClient
    {
        private readonly Channel<ChatResponseUpdate> _updates = Channel.CreateUnbounded<ChatResponseUpdate>();
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ChatMessage> Input { get; private set; } = [];
        public bool CallToolFirst { get; init; }
        public bool IncludeMessageIds { get; init; } = true;

        public void Emit(string text) =>
            _updates.Writer.TryWrite(
                new ChatResponseUpdate(ChatRole.Assistant, text)
                {
                    MessageId = IncludeMessageIds ? "answer-1" : null,
                    ResponseId = "response-1",
                }
            );

        public void Finish(Exception? failure = null) => _updates.Writer.TryComplete(failure);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Input = messages.ToList();
            if (CallToolFirst && _calls++ == 0)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())]
                )
                {
                    MessageId = "tool-call",
                    ResponseId = "tool-response",
                    FinishReason = ChatFinishReason.ToolCalls,
                };
                yield break;
            }
            Started.TrySetResult();
            await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
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
