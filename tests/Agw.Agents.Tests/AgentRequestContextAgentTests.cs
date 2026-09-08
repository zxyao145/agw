using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Projects.Domain.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed partial class AgentRequestContextAgentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_WithMemory_PersistsOriginalOnceAndForwardsComposite(bool streaming)
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var innerAgent = new HistoryNotifyingAgent(fixture.Provider);
        var agent = CreateAgent(innerAgent, fixture.Provider, "private memory");
        var session = await InitializeHistorySessionAsync(agent, fixture);
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
        Assert.Equal(request.MessageId, records[0].ToChatMessage()!.MessageId);
        Assert.Equal(records[0].TaskId, records[1].TaskId);
        await fixture.Provider.PersistPendingAsync(agent, session, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await fixture.ReadAsync()).Count);
    }

    [Fact]
    public async Task RunAsync_InnerDoesNotNotifyHistory_FallbackPersistsOriginalRequest()
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var innerAgent = new HistoryNotifyingAgent(historyProvider: null);
        var agent = CreateAgent(innerAgent, fixture.Provider, memoryText: null);
        var session = await InitializeHistorySessionAsync(agent, fixture);
        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "current request")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(ConversationHistoryMetadata.IsPersistenceExcluded(Assert.Single(innerAgent.RequestMessages)));
        Assert.Equal("current request", Assert.Single(await fixture.ReadAsync()).GetText());
    }

    [Fact]
    public async Task HistoryProvider_StagedInputAndInjectedContext_PreservesOriginalAndExistingSourceFilter()
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var agent = new HistoryNotifyingAgent(historyProvider: null);
        var session = await InitializeHistorySessionAsync(agent, fixture);
        fixture.Provider.StageRequest(session, [new ChatMessage(ChatRole.User, "original")]);
        var transient = new ChatMessage(ChatRole.User, "transient");
        ConversationHistoryMetadata.ExcludeFromPersistence(transient);
        var context = new ChatMessage(ChatRole.User, "injected context").WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            "ContextProvider"
        );
        await fixture.Provider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(
                agent,
                session,
                [transient, context],
                [new ChatMessage(ChatRole.Assistant, "answer")]
            ),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            ["original", "injected context", "answer"],
            (await fixture.ReadAsync()).Select(record => record.GetText())
        );
    }

    [Fact]
    public async Task RunAsync_InnerReportsFailure_PersistsOriginalAndPreservesInvokeException()
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var failure = new InvalidOperationException("SDK failure");
        var innerAgent = new HistoryNotifyingAgent(fixture.Provider, failure);
        var agent = CreateAgent(innerAgent, fixture.Provider, "private memory");
        var session = await InitializeHistorySessionAsync(agent, fixture);
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
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var chatClient = new CapturingChatClient();
        await using var capabilities = CreateCapabilities();
        var innerAgent = chatClient.AsAgwAgent(
            CreateDefinition(fixture.Provider),
            capabilities,
            NullLoggerFactory.Instance,
            fixture.Services
        );
        var agent = CreateAgent(innerAgent, fixture.Provider, "private memory");
        var session = await InitializeHistorySessionAsync(agent, fixture);
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
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var innerAgent = new HistoryNotifyingAgent(fixture.Provider);
        var agent = CreateAgent(innerAgent, fixture.Provider, memoryText: null);
        var session = await InitializeHistorySessionAsync(agent, fixture);
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
    public async Task StageRequest_FreezesInputOutsideSessionStateAndPersistsOnce()
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var agent = new HistoryNotifyingAgent(historyProvider: null);
        var session = await InitializeHistorySessionAsync(agent, fixture);
        var input = new ChatMessage(ChatRole.User, "sensitive current request");
        fixture.Provider.StageRequest(session, [input]);
        input.Contents.Clear();
        Assert.DoesNotContain(
            "sensitive current request",
            session.StateBag.Serialize().GetRawText(),
            StringComparison.Ordinal
        );
        await fixture.Provider.PersistPendingAsync(agent, session, TestContext.Current.CancellationToken);
        await fixture.Provider.PersistPendingAsync(agent, session, TestContext.Current.CancellationToken);
        Assert.Equal("sensitive current request", Assert.Single(await fixture.ReadAsync()).GetText());
    }

    [Fact]
    public async Task RunStreamingAsync_GetAsyncEnumeratorThrows_PersistsOriginalAndPreservesFailure()
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var failure = new InvalidOperationException("enumerator failure");
        var agent = CreateAgent(new GetEnumeratorThrowingAgent(failure), fixture.Provider, memoryText: null);
        var session = await InitializeHistorySessionAsync(agent, fixture);
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

    private static IDisposable EnterHistoryUser() =>
        UserInfoUtil.Push(
            new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "tester")],
                    "Test"
                )
            )
        );

    private static async Task<AgentSession> InitializeHistorySessionAsync(
        AIAgent agent,
        StreamingHistoryFixture fixture
    )
    {
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        fixture.Provider.InitializeSessionState(session, "streaming", fixture.ProjectId);
        return session;
    }

    private static AgentRequestContextAgent CreateAgent(
        AIAgent innerAgent,
        ChatHistoryProvider historyProvider,
        string? memoryText
    ) =>
        new(
            innerAgent,
            historyProvider,
            memoryText == null
                ? null
                : _ =>
                    ValueTask.FromResult<ChatMessage?>(
                        new ChatMessage(ChatRole.User, memoryText).WithAgentRequestMessageSource(
                            AgentRequestMessageSourceType.AIContextProvider,
                            ConversationHistoryMetadata.UserMemorySourceId
                        )
                    ),
            NullLogger<AgentRequestContextAgent>.Instance
        );

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

    private sealed class HistoryNotifyingAgent : AIAgent
    {
        private readonly ChatHistoryProvider? _historyProvider;
        private readonly Exception? _invokeException;

        public HistoryNotifyingAgent(ChatHistoryProvider? historyProvider, Exception? invokeException = null)
        {
            _historyProvider = historyProvider;
            _invokeException = invokeException;
        }

        public IReadOnlyList<ChatMessage> RequestMessages { get; private set; } = [];

        public AgentSession? Session { get; private set; }

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

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        )
        {
            RequestMessages = messages.ToList();
            Session = session;
            var response = new ChatMessage(ChatRole.Assistant, "answer");
            if (_historyProvider != null)
            {
                var context =
                    _invokeException == null
                        ? new ChatHistoryProvider.InvokedContext(this, session, RequestMessages, [response])
                        : new ChatHistoryProvider.InvokedContext(this, session, RequestMessages, _invokeException);
                await _historyProvider.InvokedAsync(context, cancellationToken);
            }
            if (_invokeException != null)
            {
                throw _invokeException;
            }
            return new AgentResponse([response]);
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            RequestMessages = messages.ToList();
            Session = session;
            var response = new ChatMessage(ChatRole.Assistant, "answer");
            if (_historyProvider != null)
            {
                await _historyProvider.InvokedAsync(
                    new ChatHistoryProvider.InvokedContext(this, session, RequestMessages, [response]),
                    cancellationToken
                );
            }
            yield return new AgentResponseUpdate(ChatRole.Assistant, "answer");
        }

        private sealed class TestSession : AgentSession;
    }

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
}
