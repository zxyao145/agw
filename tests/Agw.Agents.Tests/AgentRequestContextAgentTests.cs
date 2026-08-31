using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class AgentRequestContextAgentTests
{
    [Fact]
    public async Task RunAsync_WithMemory_PersistsOriginalOnceAndForwardsComposite()
    {
        // Arrange
        var recordingHistory = new RecordingChatHistoryProvider();
        var requestHistory = new AgentRequestChatHistoryProvider(recordingHistory);
        var innerAgent = new HistoryNotifyingAgent(requestHistory);
        var agent = CreateAgent(innerAgent, requestHistory, "private memory");
        var request = new ChatMessage(ChatRole.User, "current request") { MessageId = "request-1" };

        // Act
        await agent.RunAsync([request], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var forwarded = Assert.Single(innerAgent.RequestMessages);
        Assert.Equal("private memory\n\n## Current Request\n\ncurrent request", forwarded.Text);
        Assert.True(ConversationHistoryMetadata.IsPersistenceExcluded(forwarded));
        Assert.False(ConversationHistoryMetadata.IsPersistenceExcluded(request));
        Assert.Equal(AgentRequestMessageSourceType.External, request.GetAgentRequestMessageSourceType());
        var historyCall = Assert.Single(recordingHistory.Calls);
        Assert.Equal(request.MessageId, Assert.Single(historyCall.RequestMessages).MessageId);
        Assert.Equal("answer", Assert.Single(historyCall.ResponseMessages).Text);
        await requestHistory.PersistPendingAsync(agent, innerAgent.Session!, TestContext.Current.CancellationToken);
        Assert.Single(recordingHistory.Calls);
    }

    [Fact]
    public async Task RunStreamingAsync_WithMemory_PersistsOriginalOnceAndForwardsComposite()
    {
        // Arrange
        var recordingHistory = new RecordingChatHistoryProvider();
        var requestHistory = new AgentRequestChatHistoryProvider(recordingHistory);
        var innerAgent = new HistoryNotifyingAgent(requestHistory);
        var agent = CreateAgent(innerAgent, requestHistory, "private memory");
        var request = new ChatMessage(ChatRole.User, "current request") { MessageId = "request-1" };

        // Act
        var updates = new List<AgentResponseUpdate>();
        await foreach (
            var update in agent.RunStreamingAsync([request], cancellationToken: TestContext.Current.CancellationToken)
        )
        {
            updates.Add(update);
        }

        // Assert
        Assert.Equal(
            "private memory\n\n## Current Request\n\ncurrent request",
            Assert.Single(innerAgent.RequestMessages).Text
        );
        var historyCall = Assert.Single(recordingHistory.Calls);
        Assert.Equal(request.MessageId, Assert.Single(historyCall.RequestMessages).MessageId);
        Assert.Equal("answer", Assert.Single(historyCall.ResponseMessages).Text);
        Assert.Equal("answer", Assert.Single(updates).Text);
    }

    [Fact]
    public async Task RunAsync_InnerDoesNotNotifyHistory_FallbackPersistsOriginalRequest()
    {
        // Arrange
        var recordingHistory = new RecordingChatHistoryProvider();
        var requestHistory = new AgentRequestChatHistoryProvider(recordingHistory);
        var innerAgent = new HistoryNotifyingAgent(historyProvider: null);
        var agent = CreateAgent(innerAgent, requestHistory, memoryText: null);
        var request = new ChatMessage(ChatRole.User, "current request");

        // Act
        await agent.RunAsync([request], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.True(ConversationHistoryMetadata.IsPersistenceExcluded(Assert.Single(innerAgent.RequestMessages)));
        var historyCall = Assert.Single(recordingHistory.Calls);
        Assert.Equal(request.Text, Assert.Single(historyCall.RequestMessages).Text);
        Assert.Empty(historyCall.ResponseMessages);
    }

    [Fact]
    public async Task HistoryProvider_NonTransientContextMessage_PreservesItWithOriginalRequest()
    {
        // Arrange
        var recordingHistory = new RecordingChatHistoryProvider();
        var historyProvider = new AgentRequestChatHistoryProvider(recordingHistory);
        var agent = new HistoryNotifyingAgent(historyProvider: null);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        historyProvider.StageRequest(session, [new ChatMessage(ChatRole.User, "original")]);
        var transient = new ChatMessage(ChatRole.User, "transient");
        ConversationHistoryMetadata.ExcludeFromPersistence(transient);
        var retainedContext = new ChatMessage(ChatRole.User, "retained context").WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            "RetainedProvider"
        );

        // Act
        await historyProvider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(
                agent,
                session,
                [transient, retainedContext],
                [new ChatMessage(ChatRole.Assistant, "answer")]
            ),
            TestContext.Current.CancellationToken
        );

        // Assert
        var call = Assert.Single(recordingHistory.Calls);
        Assert.Equal(["original", "retained context"], call.RequestMessages.Select(message => message.Text));
        Assert.Equal("answer", Assert.Single(call.ResponseMessages).Text);
    }

    [Fact]
    public async Task RunAsync_InnerReportsFailure_PersistsOriginalAndPreservesInvokeException()
    {
        // Arrange
        var failure = new InvalidOperationException("SDK failure");
        var recordingHistory = new RecordingChatHistoryProvider();
        var requestHistory = new AgentRequestChatHistoryProvider(recordingHistory);
        var innerAgent = new HistoryNotifyingAgent(requestHistory, failure);
        var agent = CreateAgent(innerAgent, requestHistory, "private memory");
        var request = new ChatMessage(ChatRole.User, "current request");

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.RunAsync([request], cancellationToken: TestContext.Current.CancellationToken)
        );

        // Assert
        Assert.Same(failure, thrown);
        Assert.Collection(
            recordingHistory.Calls,
            persisted =>
            {
                Assert.Null(persisted.InvokeException);
                Assert.Equal(request.Text, Assert.Single(persisted.RequestMessages).Text);
                Assert.Empty(persisted.ResponseMessages);
            },
            failed => Assert.Same(failure, failed.InvokeException)
        );
    }

    [Fact]
    public async Task RunAsync_SystemChatClientPipeline_PreservesTransientMarkerAndPersistsOnlyOriginalRequest()
    {
        // Arrange
        var recordingHistory = new RecordingChatHistoryProvider();
        var requestHistory = new AgentRequestChatHistoryProvider(recordingHistory);
        var chatClient = new CapturingChatClient();
        await using var capabilities = CreateCapabilities();
        using var services = new ServiceCollection().BuildServiceProvider();
        var innerAgent = chatClient.AsAgwAgent(
            CreateDefinition(requestHistory),
            capabilities,
            NullLoggerFactory.Instance,
            services
        );
        var agent = CreateAgent(innerAgent, requestHistory, "private memory");
        var request = new ChatMessage(ChatRole.User, "current request") { MessageId = "request-1" };

        // Act
        await agent.RunAsync([request], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(chatClient.Requests);
        var historyCall = Assert.Single(recordingHistory.Calls);
        Assert.Equal(request.MessageId, Assert.Single(historyCall.RequestMessages).MessageId);
        Assert.DoesNotContain(
            historyCall.RequestMessages,
            message => message.Text?.Contains("private memory", StringComparison.Ordinal) == true
        );
        Assert.Equal("answer", Assert.Single(historyCall.ResponseMessages).Text);
    }

    [Theory]
    [InlineData("once")]
    [InlineData("always-tool")]
    [InlineData("always-arguments")]
    public async Task RunAsync_FunctionApprovalResponse_PersistsDisplayOnlyResponseAndForwardsOriginal(
        string approvalScope
    )
    {
        // Arrange
        var recordingHistory = new RecordingChatHistoryProvider();
        var requestHistory = new AgentRequestChatHistoryProvider(recordingHistory);
        var innerAgent = new HistoryNotifyingAgent(requestHistory);
        var agent = CreateAgent(innerAgent, requestHistory, memoryText: null);
        var toolCall = new FunctionCallContent(
            "call-1",
            "read_file",
            new Dictionary<string, object?> { ["path"] = "README.md" }
        );
        var approvalRequest = new ToolApprovalRequestContent("approval-1", toolCall);
        AIContent approval = approvalScope switch
        {
            "once" => approvalRequest.CreateResponse(approved: true),
            "always-tool" => approvalRequest.CreateAlwaysApproveToolResponse(),
            _ => approvalRequest.CreateAlwaysApproveToolWithArgumentsResponse(),
        };
        var persistedApproval = approval is AlwaysApproveToolApprovalResponseContent alwaysApproval
            ? alwaysApproval.InnerResponse
            : (ToolApprovalResponseContent)approval;
        persistedApproval.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["approvalScope"] = approvalScope,
        };
        var request = new ChatMessage(ChatRole.User, [approval]) { MessageId = "approval-response-1" };

        // Act
        await agent.RunAsync([request], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var forwardedMessage = Assert.Single(innerAgent.RequestMessages);
        Assert.Same(approval, Assert.Single(forwardedMessage.Contents));
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(forwardedMessage));

        var persistedMessage = Assert.Single(Assert.Single(recordingHistory.Calls).RequestMessages);
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(persistedMessage));
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var serialized = JsonSerializer.Serialize(persistedMessage, jsonOptions);
        var restoredMessage = JsonSerializer.Deserialize<ChatMessage>(serialized, jsonOptions);
        Assert.NotNull(restoredMessage);
        var restoredApproval = Assert.IsType<ToolApprovalResponseContent>(Assert.Single(restoredMessage.Contents));
        Assert.True(restoredApproval.Approved);
        Assert.Equal("approval-1", restoredApproval.RequestId);
        Assert.Equal(approvalScope, restoredApproval.AdditionalProperties!["approvalScope"]?.ToString());
    }

    [Fact]
    public async Task StageRequest_DoesNotStoreRequestInSessionStateBag()
    {
        // Arrange
        var recordingHistory = new RecordingChatHistoryProvider();
        var requestHistory = new AgentRequestChatHistoryProvider(recordingHistory);
        var agent = new HistoryNotifyingAgent(historyProvider: null);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // Act
        requestHistory.StageRequest(session, [new ChatMessage(ChatRole.User, "sensitive current request")]);

        // Assert
        Assert.DoesNotContain(
            "sensitive current request",
            session.StateBag.Serialize().GetRawText(),
            StringComparison.Ordinal
        );
        await requestHistory.PersistPendingAsync(agent, session, TestContext.Current.CancellationToken);
        Assert.Equal(
            "sensitive current request",
            Assert.Single(Assert.Single(recordingHistory.Calls).RequestMessages).Text
        );
    }

    [Fact]
    public async Task RunStreamingAsync_GetAsyncEnumeratorThrows_PersistsOriginalAndPreservesFailure()
    {
        // Arrange
        var failure = new InvalidOperationException("enumerator failure");
        var recordingHistory = new RecordingChatHistoryProvider();
        var requestHistory = new AgentRequestChatHistoryProvider(recordingHistory);
        var innerAgent = new GetEnumeratorThrowingAgent(failure);
        var agent = CreateAgent(innerAgent, requestHistory, memoryText: null);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var request = new ChatMessage(ChatRole.User, "current request");

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (
                var _ in agent.RunStreamingAsync(
                    [request],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            ) { }
        });

        // Assert
        Assert.Same(failure, thrown);
        var historyCall = Assert.Single(recordingHistory.Calls);
        Assert.Equal(request.Text, Assert.Single(historyCall.RequestMessages).Text);
        await requestHistory.PersistPendingAsync(agent, session, TestContext.Current.CancellationToken);
        Assert.Single(recordingHistory.Calls);
    }

    private static AgentRequestContextAgent CreateAgent(
        AIAgent innerAgent,
        AgentRequestChatHistoryProvider historyProvider,
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

    private static AgentCapabilityComposition CreateCapabilities() =>
        new(
            tools: [],
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

    private sealed class RecordingChatHistoryProvider : ChatHistoryProvider
    {
        public List<HistoryCall> Calls { get; } = [];

        protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<IEnumerable<ChatMessage>>([]);

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add(
                new HistoryCall(
                    context.RequestMessages.ToList(),
                    context.ResponseMessages?.ToList() ?? [],
                    context.InvokeException
                )
            );
            return ValueTask.CompletedTask;
        }
    }

    private sealed record HistoryCall(
        IReadOnlyList<ChatMessage> RequestMessages,
        IReadOnlyList<ChatMessage> ResponseMessages,
        Exception? InvokeException
    );
}
