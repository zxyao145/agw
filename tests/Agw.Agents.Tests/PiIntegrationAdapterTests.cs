using System.Text.Json;
using Agw.Agents.Execution.Turns;
using Agw.Agents.ExternalAgents.Pi;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiAgentSdk;

namespace Agw.Agents.Tests;

public sealed class PiIntegrationAdapterTests
{
    [Fact]
    public async Task PiExtensionUiBridge_ForegroundConfirm_UsesCurrentHumanChannel()
    {
        var accessor = new HumanInteractionContextAccessor();
        var bridge = new PiExtensionUiBridge(accessor, allowInteraction: true);
        PiExtensionUiResponse? captured = null;
        var request = new PiExtensionUiRequest
        {
            Id = "request-1",
            Method = "confirm",
            Title = "Continue?",
            Message = "Proceed with the action?",
        };
        var inner = new CallbackAgent(async cancellationToken =>
        {
            captured = await bridge.HandleAsync(request, cancellationToken);
        });
        var agent = inner
            .AsBuilder()
            .Use(runFunc: bridge.BindRunAsync, runStreamingFunc: bridge.BindRunStreamingAsync)
            .Build();
        var channel = new TestHumanInteractionChannel(interaction => new HumanInteractionResponse(
            interaction.RequestId,
            Cancelled: false,
            JsonSerializer.SerializeToElement(new { confirmed = true })
        ));
        using var scope = accessor.Push(channel);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "run")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.NotNull(captured);
        Assert.True(captured.Confirmed);
        Assert.False(captured.Cancelled);
        Assert.Single(channel.Requests);
    }

    [Fact]
    public async Task PiExtensionUiBridge_BackgroundDialog_ReturnsCancelled()
    {
        var bridge = new PiExtensionUiBridge(contextAccessor: null, allowInteraction: false);
        PiExtensionUiResponse? captured = null;
        var inner = new CallbackAgent(async cancellationToken =>
        {
            captured = await bridge.HandleAsync(
                new PiExtensionUiRequest
                {
                    Id = "request-1",
                    Method = "input",
                    Title = "Value",
                },
                cancellationToken
            );
        });
        var agent = inner
            .AsBuilder()
            .Use(runFunc: bridge.BindRunAsync, runStreamingFunc: bridge.BindRunStreamingAsync)
            .Build();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "run")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(captured!.Cancelled);
    }

    [Theory]
    [InlineData("two", false)]
    [InlineData("outside", true)]
    public async Task PiExtensionUiBridge_Select_AcceptsOnlyConfiguredOptions(string value, bool expectedCancelled)
    {
        // Arrange
        var accessor = new HumanInteractionContextAccessor();
        var bridge = new PiExtensionUiBridge(accessor, allowInteraction: true);
        PiExtensionUiResponse? captured = null;
        var request = new PiExtensionUiRequest
        {
            Id = "request-1",
            Method = "select",
            Title = "Choose",
            Options = ["one", "two"],
        };
        var inner = new CallbackAgent(async cancellationToken =>
        {
            captured = await bridge.HandleAsync(request, cancellationToken);
        });
        var agent = inner
            .AsBuilder()
            .Use(runFunc: bridge.BindRunAsync, runStreamingFunc: bridge.BindRunStreamingAsync)
            .Build();
        var channel = new TestHumanInteractionChannel(interaction => new HumanInteractionResponse(
            interaction.RequestId,
            Cancelled: false,
            JsonSerializer.SerializeToElement(new { value })
        ));
        using var scope = accessor.Push(channel);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // Act
        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "run")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(expectedCancelled, captured.Cancelled);
        Assert.Equal(expectedCancelled ? null : value, captured.Value);
    }

    [Fact]
    public async Task PiChatHistoryProvider_Response_RemovesTransportDataAndMarksToolDisplayOnly()
    {
        var inner = new RecordingHistoryProvider();
        var provider = new PiChatHistoryProvider(inner);
        var agent = new CallbackAgent(_ => ValueTask.CompletedTask);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var response = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", "done") { RawRepresentation = new object() }]
        );
#pragma warning disable MAAI001
        var context = new ChatHistoryProvider.InvokedContext(agent, session, [], [response]);
#pragma warning restore MAAI001

        await provider.InvokedAsync(context, TestContext.Current.CancellationToken);

        var persisted = Assert.Single(Assert.Single(inner.Stored).Responses);
        Assert.Null(Assert.Single(persisted.Contents).RawRepresentation);
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(persisted));
    }

    [Fact]
    public async Task PiChatHistoryProvider_Request_IsIgnoredByResponseOnlyAdapter()
    {
        // Arrange
        var inner = new RecordingHistoryProvider();
        var provider = new PiChatHistoryProvider(inner);
        var agent = new CallbackAgent(_ => ValueTask.CompletedTask);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var request = new ChatMessage(ChatRole.User, [new TextContent("run") { RawRepresentation = new object() }])
        {
            RawRepresentation = new object(),
        };
#pragma warning disable MAAI001
        var context = new ChatHistoryProvider.InvokedContext(agent, session, [request], []);
#pragma warning restore MAAI001

        // Act
        await provider.InvokedAsync(context, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(Assert.Single(inner.Stored).Requests);
    }

    [Fact]
    public async Task PiChatHistoryProvider_Request_ExcludesInjectedContextFromHistory()
    {
        var inner = new RecordingHistoryProvider();
        var provider = new PiChatHistoryProvider(inner);
        var agent = new CallbackAgent(_ => ValueTask.CompletedTask);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var memory = new ChatMessage(ChatRole.User, "memory context").WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            "UserMemoryProvider"
        );
        var request = new ChatMessage(ChatRole.User, "run");
#pragma warning disable MAAI001
        var context = new ChatHistoryProvider.InvokedContext(agent, session, [memory, request], []);
#pragma warning restore MAAI001

        await provider.InvokedAsync(context, TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(inner.Stored).Requests);
    }

    [Fact]
    public async Task PiChatHistoryProvider_Failure_SanitizesRequestAndPreservesException()
    {
        // Arrange
        var inner = new RecordingHistoryProvider();
        var provider = new PiChatHistoryProvider(inner);
        var agent = new CallbackAgent(_ => ValueTask.CompletedTask);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var request = new ChatMessage(ChatRole.User, [new TextContent("run") { RawRepresentation = new object() }]);
        var failure = new InvalidOperationException("failed");
#pragma warning disable MAAI001
        var context = new ChatHistoryProvider.InvokedContext(agent, session, [request], failure);
#pragma warning restore MAAI001

        // Act
        await provider.InvokedAsync(context, TestContext.Current.CancellationToken);

        // Assert
        var stored = Assert.Single(inner.Stored);
        Assert.Same(failure, stored.InvokeException);
        Assert.Empty(stored.Requests);
    }

    private sealed class CallbackAgent : AIAgent
    {
        private readonly Func<CancellationToken, ValueTask> _callback;

        public CallbackAgent(Func<CancellationToken, ValueTask> callback)
        {
            _callback = callback;
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

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        )
        {
            await _callback(cancellationToken);
            return new AgentResponse();
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await _callback(cancellationToken);
            yield break;
        }

        private sealed class TestSession : AgentSession;
    }

    private sealed class TestHumanInteractionChannel : IHumanInteractionChannel
    {
        private readonly Func<HumanInteractionRequest, HumanInteractionResponse> _respond;

        public TestHumanInteractionChannel(Func<HumanInteractionRequest, HumanInteractionResponse> respond)
        {
            _respond = respond;
        }

        public List<HumanInteractionRequest> Requests { get; } = [];

        public ValueTask<HumanInteractionResponse> RequestAsync(
            HumanInteractionRequest request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return ValueTask.FromResult(_respond(request));
        }
    }

    private sealed class RecordingHistoryProvider : ChatHistoryProvider
    {
        public List<StoredCall> Stored { get; } = [];

        protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<IEnumerable<ChatMessage>>([]);

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default
        )
        {
            Stored.Add(
                new StoredCall(
                    context.RequestMessages.ToList(),
                    context.ResponseMessages?.ToList() ?? [],
                    context.InvokeException
                )
            );
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StoredCall
    {
        public StoredCall(
            IReadOnlyList<ChatMessage> requests,
            IReadOnlyList<ChatMessage> responses,
            Exception? invokeException
        )
        {
            Requests = requests;
            Responses = responses;
            InvokeException = invokeException;
        }

        public IReadOnlyList<ChatMessage> Requests { get; }

        public IReadOnlyList<ChatMessage> Responses { get; }

        public Exception? InvokeException { get; }
    }
}
