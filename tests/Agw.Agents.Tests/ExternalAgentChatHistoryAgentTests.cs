using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class ExternalAgentChatHistoryAgentTests
{
    [Fact]
    public async Task RunStreamingAsync_BeforeFirstUpdate_PersistsRequest()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var firstUpdate = enumerator.MoveNextAsync().AsTask();
        await innerAgent.Started.WaitAsync(TestContext.Current.CancellationToken);

        var requestCall = Assert.Single(provider.Calls);
        Assert.Equal("request", Assert.Single(requestCall.RequestMessages).Text);
        Assert.Empty(requestCall.ResponseMessages);
        Assert.Same(session, requestCall.Session);
        Assert.Equal("External", requestCall.AgentName);
        Assert.False(firstUpdate.IsCompleted);

        innerAgent.Complete();
        Assert.False(await firstUpdate);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenTwentyMessagesArrive_FlushesOneOrderedBatch()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        for (var index = 0; index < 20; index++)
        {
            innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, $"update-{index}"));
        }

        for (var index = 0; index < 20; index++)
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        var responseCall = Assert.Single(provider.Calls, call => call.ResponseMessages.Count > 0);
        Assert.Empty(responseCall.RequestMessages);
        Assert.Equal(
            Enumerable.Range(0, 20).Select(index => $"update-{index}"),
            responseCall.ResponseMessages.Select(message => message.Text)
        );

        innerAgent.Complete();
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(2, provider.Calls.Count);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenExternalAgentPauses_FlushesAfterOneSecond()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "update"));
        Assert.True(await enumerator.MoveNextAsync());

        var pendingUpdate = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        Assert.Single(provider.Calls);
        await provider.WaitForCallCountAsync(2, TimeSpan.FromSeconds(3));

        var responseCall = provider.Calls[1];
        Assert.Equal("update", Assert.Single(responseCall.ResponseMessages).Text);
        Assert.False(pendingUpdate.IsCompleted);

        innerAgent.Complete();
        Assert.False(await pendingUpdate);
        Assert.Equal(2, provider.Calls.Count);
    }

    [Fact]
    public async Task RunStreamingAsync_OnNormalCompletion_FlushesRemainderWithoutTurnEndDuplicate()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "first"));
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "second"));
        innerAgent.Complete();

        var updates = await CollectAsync(
            agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(["first", "second"], updates.Select(update => update.Text));
        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal(["first", "second"], provider.Calls[1].ResponseMessages.Select(message => message.Text));
    }

    [Fact]
    public async Task RunStreamingAsync_WhenCancelled_FlushesProducedMessages()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: cancellationSource.Token
            )
            .GetAsyncEnumerator(cancellationSource.Token);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "before cancellation"));
        Assert.True(await enumerator.MoveNextAsync());

        var pendingUpdate = enumerator.MoveNextAsync().AsTask();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingUpdate);
        Assert.Equal("before cancellation", Assert.Single(provider.Calls[1].ResponseMessages).Text);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenInnerAgentFails_FlushesProducedMessagesAndPreservesFailure()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "before failure"));
        Assert.True(await enumerator.MoveNextAsync());
        innerAgent.Fail(new InvalidOperationException("external failure"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal("external failure", exception.Message);
        Assert.Equal("before failure", Assert.Single(provider.Calls[1].ResponseMessages).Text);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenConsumerDisposesEarly_FlushesProducedMessages()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "before disposal"));
        Assert.True(await enumerator.MoveNextAsync());

        await enumerator.DisposeAsync();

        Assert.Equal("before disposal", Assert.Single(provider.Calls[1].ResponseMessages).Text);
        Assert.True(innerAgent.StreamDisposed);
    }

    [Fact]
    public async Task RunStreamingAsync_DisplayOnlyEvents_AreMarkedAndEmptyControlEventsAreSkipped()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        innerAgent.Emit(
            new AgentResponseUpdate(ChatRole.System, "Todo list")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = "todo" },
            }
        );
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "answer"));
        innerAgent.Emit(new AgentResponseUpdate { Role = ChatRole.System });
        innerAgent.Complete();

        await CollectAsync(
            agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        var messages = Assert.Single(provider.Calls, call => call.ResponseMessages.Count > 0).ResponseMessages;
        Assert.Equal(2, messages.Count);
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(messages[0]));
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(messages[1]));
        Assert.Equal(["Todo list", "answer"], messages.Select(message => message.Text));
    }

    [Fact]
    public async Task RunStreamingAsync_ClaudeInit_CapturesProviderSessionOnceWithoutChangingUpdates()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var capturedSessionIds = new List<string>();
        var agent = CreateAgent(
            innerAgent,
            provider,
            (providerSessionId, _) =>
            {
                capturedSessionIds.Add(providerSessionId);
                return ValueTask.CompletedTask;
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var expectedSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        innerAgent.Emit(CreateClaudeInitUpdate(expectedSessionId));
        innerAgent.Emit(CreateClaudeInitUpdate(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        innerAgent.Complete();

        var updates = await CollectAsync(
            agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(2, updates.Count);
        Assert.Equal(expectedSessionId.Normalize(), Assert.Single(capturedSessionIds));
        var persistedMessages = Assert.Single(provider.Calls, call => call.ResponseMessages.Count > 0).ResponseMessages;
        Assert.Equal(2, persistedMessages.Count);
    }

    [Fact]
    public async Task RunAsync_ClaudeInit_CapturesProviderSession()
    {
        var provider = new RecordingChatHistoryProvider();
        var expectedSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var innerAgent = new PausableExternalAgent
        {
            NonStreamingMessages =
            [
                CreateClaudeInitMessage(expectedSessionId),
                new ChatMessage(ChatRole.Assistant, "non-streaming answer"),
            ],
        };
        string? capturedSessionId = null;
        var agent = CreateAgent(
            innerAgent,
            provider,
            (providerSessionId, _) =>
            {
                capturedSessionId = providerSessionId;
                return ValueTask.CompletedTask;
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "request")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(expectedSessionId.Normalize(), capturedSessionId);
        Assert.Equal(2, response.Messages.Count);
        Assert.Equal(2, Assert.Single(provider.Calls, call => call.ResponseMessages.Count > 0).ResponseMessages.Count);
    }

    [Fact]
    public async Task RunStreamingAsync_InvalidClaudeInit_DoesNotCaptureOrChangeUpdates()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var callbackCount = 0;
        var agent = CreateAgent(
            innerAgent,
            provider,
            (_, _) =>
            {
                callbackCount++;
                return ValueTask.CompletedTask;
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        innerAgent.Emit(CreateClaudeInitUpdate("{bad json"));
        innerAgent.Emit(CreateClaudeInitUpdate(JsonSerializer.Serialize(new { session_id = "not-a-guid" })));
        innerAgent.Emit(CreateClaudeInitUpdate(JsonSerializer.Serialize(new { tools = Array.Empty<string>() })));
        innerAgent.Emit(
            CreateClaudeInitUpdate(
                JsonSerializer.Serialize(new { session_id = Guid.CreateVersion7() }),
                subtype: "status"
            )
        );
        innerAgent.Complete();

        var updates = await CollectAsync(
            agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(4, updates.Count);
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task RunStreamingAsync_ClaudeInitBeforeFailure_CapturesSessionAndPreservesFailure()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var expectedSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        string? capturedSessionId = null;
        var agent = CreateAgent(
            innerAgent,
            provider,
            (providerSessionId, _) =>
            {
                capturedSessionId = providerSessionId;
                return ValueTask.CompletedTask;
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        innerAgent.Emit(CreateClaudeInitUpdate(expectedSessionId));
        Assert.True(await enumerator.MoveNextAsync());
        innerAgent.Fail(new InvalidOperationException("429 quota exceeded"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal(expectedSessionId.Normalize(), capturedSessionId);
        Assert.Equal("429 quota exceeded", exception.Message);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenFinalPersistenceFailsDuringExecutionFailure_PreservesExecutionFailure()
    {
        var provider = new RecordingChatHistoryProvider { FailureCallNumber = 2 };
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "before failure"));
        Assert.True(await enumerator.MoveNextAsync());
        innerAgent.Fail(new InvalidOperationException("external failure"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal("external failure", exception.Message);
        Assert.Equal(2, provider.AttemptCount);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenFinalPersistenceFailsWithoutExecutionFailure_FailsTurn()
    {
        var provider = new RecordingChatHistoryProvider { FailureCallNumber = 2 };
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "answer"));
        Assert.True(await enumerator.MoveNextAsync());
        innerAgent.Complete();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal("persistence failure", exception.Message);
        Assert.Equal(2, provider.AttemptCount);
    }

    [Fact]
    public async Task RunStreamingAsync_BlockParticipant_UsesParticipantHistoryScopeAndNodeName()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var externalAgent = CreateAgent(innerAgent, provider);
        var providerSessionState = new CapturingProviderSessionState();
        var agentflowId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var scopedAgent = new AgentflowNodeScopedAgent(
            externalAgent,
            "group.participant",
            "Participant",
            instructions: null,
            new AgentflowAgentSessionScope(providerSessionState, projectId, "context-1", taskId: null),
            agentflowId: agentflowId,
            agentId: Guid.CreateVersion7(),
            historyNodeId: "participant"
        );
        var response = await scopedAgent.RunAsync(
            [new ChatMessage(ChatRole.User, "request")],
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("non-streaming answer", Assert.Single(response.Messages).Text);
        Assert.Equal(projectId, providerSessionState.ProjectId);
        Assert.Equal("context-1", providerSessionState.ContextId);
        Assert.Equal($"agentflow:{agentflowId:N}:node:participant", providerSessionState.HistoryScope);
        Assert.Equal("Participant", providerSessionState.NodeName);
        Assert.All(provider.Calls, call => Assert.Same(providerSessionState.Session, call.Session));
    }

    private static ExternalAgentChatHistoryAgent CreateAgent(
        AIAgent innerAgent,
        ChatHistoryProvider provider,
        Func<string, CancellationToken, ValueTask>? onProviderSessionStartedAsync = null
    ) =>
        new(
            innerAgent,
            provider,
            TimeProvider.System,
            NullLogger<ExternalAgentChatHistoryAgent>.Instance,
            onProviderSessionStartedAsync
        );

    private static AgentResponseUpdate CreateClaudeInitUpdate(Guid sessionId) =>
        CreateClaudeInitUpdate(JsonSerializer.Serialize(new { session_id = sessionId }));

    private static AgentResponseUpdate CreateClaudeInitUpdate(string content, string subtype = "init") =>
        new(ChatRole.System, content)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["subtype"] = subtype },
        };

    private static ChatMessage CreateClaudeInitMessage(Guid sessionId) =>
        new(ChatRole.System, JsonSerializer.Serialize(new { session_id = sessionId }))
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["subtype"] = "init" },
        };

    private static async Task<List<AgentResponseUpdate>> CollectAsync(IAsyncEnumerable<AgentResponseUpdate> updates)
    {
        var result = new List<AgentResponseUpdate>();
        await foreach (var update in updates)
        {
            result.Add(update);
        }

        return result;
    }

    private sealed class RecordingChatHistoryProvider : ChatHistoryProvider
    {
        private readonly SemaphoreSlim _callsChanged = new(0);

        public List<HistoryCall> Calls { get; } = [];

        public int AttemptCount { get; private set; }

        public int? FailureCallNumber { get; init; }

        public async Task WaitForCallCountAsync(int count, TimeSpan timeout)
        {
            using var cancellationSource = new CancellationTokenSource(timeout);
            while (Calls.Count < count)
            {
                await _callsChanged.WaitAsync(cancellationSource.Token);
            }
        }

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<IEnumerable<ChatMessage>>([]);

        protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken)
        {
            AttemptCount++;
            if (FailureCallNumber == AttemptCount)
            {
                throw new InvalidOperationException("persistence failure");
            }

            Calls.Add(
                new HistoryCall(
                    context.Agent.Name,
                    context.Session,
                    context.RequestMessages.ToList(),
                    context.ResponseMessages?.ToList() ?? []
                )
            );
            _callsChanged.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record HistoryCall(
        string? AgentName,
        AgentSession? Session,
        IReadOnlyList<ChatMessage> RequestMessages,
        IReadOnlyList<ChatMessage> ResponseMessages
    );

    private sealed class CapturingProviderSessionState : IProviderSessionState
    {
        public AgentSession? Session { get; private set; }

        public Guid ProjectId { get; private set; }

        public string? ContextId { get; private set; }

        public string? HistoryScope { get; private set; }

        public string? NodeName { get; private set; }

        public void InitializeSessionState(AgentSession session, string contextId, Guid projectId)
        {
            Session = session;
            ProjectId = projectId;
            ContextId = contextId;
        }

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope
        ) => InitializeSessionState(session, contextId, projectId, historyScope, nodeName: null);

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope,
            string? nodeName
        )
        {
            InitializeSessionState(session, contextId, projectId);
            HistoryScope = historyScope;
            NodeName = nodeName;
        }
    }

    private sealed class PausableExternalAgent : AIAgent
    {
        private readonly Channel<AgentResponseUpdate> _updates = Channel.CreateUnbounded<AgentResponseUpdate>();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public bool StreamDisposed { get; private set; }

        public IReadOnlyList<ChatMessage> NonStreamingMessages { get; init; } =
        [new ChatMessage(ChatRole.Assistant, "non-streaming answer")];

        public void Emit(AgentResponseUpdate update) => Assert.True(_updates.Writer.TryWrite(update));

        public void Complete() => Assert.True(_updates.Writer.TryComplete());

        public void Fail(Exception exception) => Assert.True(_updates.Writer.TryComplete(exception));

        protected override string? IdCore => "external";

        public override string? Name => "External";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ExternalSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new ExternalSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => Task.FromResult(new AgentResponse(NonStreamingMessages.ToList()));

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            _started.TrySetResult();
            try
            {
                await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return update;
                }
            }
            finally
            {
                StreamDisposed = true;
            }
        }

        private sealed class ExternalSession : AgentSession;
    }
}
