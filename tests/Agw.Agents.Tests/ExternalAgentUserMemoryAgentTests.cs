using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents;
using Agw.Agents.ExternalAgents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class ExternalAgentUserMemoryAgentTests
{
    [Theory]
    [InlineData(nameof(ExternalAgentKind.Codex))]
    [InlineData(nameof(ExternalAgentKind.Pi))]
    public async Task RunAsync_MultiMessageExternalAgent_PrependsAttributedMemoryWithoutChangingRequest(string kindName)
    {
        var kind = Enum.Parse<ExternalAgentKind>(kindName);
        var innerAgent = new CapturingAgent();
        var agent = new ExternalAgentUserMemoryAgent(innerAgent, new StaticContextProvider("memory context"), kind);
        var request = new ChatMessage(ChatRole.User, "current request");

        await agent.RunAsync([request], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Collection(
            innerAgent.RequestMessages,
            memory =>
            {
                Assert.Equal("memory context", memory.Text);
                Assert.Equal(
                    AgentRequestMessageSourceType.AIContextProvider,
                    memory.GetAgentRequestMessageSourceType()
                );
            },
            current => Assert.Same(request, current)
        );
        Assert.Equal(AgentRequestMessageSourceType.External, request.GetAgentRequestMessageSourceType());
    }

    [Fact]
    public async Task RunAsync_ClaudeCode_PrependsCompositePromptAndPreservesOriginalForHistory()
    {
        var innerAgent = new CapturingAgent();
        var agent = new ExternalAgentUserMemoryAgent(
            innerAgent,
            new StaticContextProvider("memory context"),
            ExternalAgentKind.ClaudeCode
        );
        var request = new ChatMessage(ChatRole.User, "current request")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["custom"] = "value" },
        };

        await agent.RunAsync([request], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Collection(
            innerAgent.RequestMessages,
            composite =>
            {
                Assert.Equal("memory context\n\n## Current Request\n\ncurrent request", composite.Text);
                Assert.Equal(
                    AgentRequestMessageSourceType.AIContextProvider,
                    composite.GetAgentRequestMessageSourceType()
                );
                Assert.Equal("value", composite.AdditionalProperties!["custom"]);
            },
            current => Assert.Same(request, current)
        );
        Assert.Equal(AgentRequestMessageSourceType.External, request.GetAgentRequestMessageSourceType());
        Assert.Equal("value", request.AdditionalProperties!["custom"]);
    }

    [Fact]
    public async Task RunStreamingAsync_ClaudeCode_InjectsMemoryBeforeStreaming()
    {
        var innerAgent = new CapturingAgent();
        var agent = new ExternalAgentUserMemoryAgent(
            innerAgent,
            new StaticContextProvider("memory context"),
            ExternalAgentKind.ClaudeCode
        );

        var updates = new List<AgentResponseUpdate>();
        await foreach (
            var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "current request")],
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            updates.Add(update);
        }

        Assert.Equal("memory context\n\n## Current Request\n\ncurrent request", innerAgent.RequestMessages[0].Text);
        Assert.Equal("answer", Assert.Single(updates).Text);
    }

    [Fact]
    public async Task RunAsync_NoMemoryMessages_ForwardsOriginalRequestOnly()
    {
        var innerAgent = new CapturingAgent();
        var agent = new ExternalAgentUserMemoryAgent(
            innerAgent,
            new StaticContextProvider(null),
            ExternalAgentKind.Codex
        );
        var request = new ChatMessage(ChatRole.User, "current request");

        await agent.RunAsync([request], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(request, Assert.Single(innerAgent.RequestMessages));
    }

    [Fact]
    public async Task RunAsync_ExternalHistoryWrapper_PersistsOriginalRequestWithoutInjectedMemory()
    {
        var innerAgent = new CapturingAgent();
        var historyProvider = new RecordingChatHistoryProvider();
        var historyAgent = new ExternalAgentChatHistoryAgent(
            innerAgent,
            historyProvider,
            TimeProvider.System,
            NullLogger<ExternalAgentChatHistoryAgent>.Instance
        );
        var agent = new ExternalAgentUserMemoryAgent(
            historyAgent,
            new StaticContextProvider("memory context"),
            ExternalAgentKind.Codex
        );
        var request = new ChatMessage(ChatRole.User, "current request");

        await agent.RunAsync([request], cancellationToken: TestContext.Current.CancellationToken);

        var persistedRequest = Assert.Single(historyProvider.Calls, call => call.RequestMessages.Count > 0);
        Assert.Same(request, Assert.Single(persistedRequest.RequestMessages));
        Assert.DoesNotContain(
            historyProvider.Calls.SelectMany(call => call.RequestMessages),
            message => message.Text.Contains("memory context", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task DisposeAsync_DisposesInnerAgentOnce()
    {
        var innerAgent = new CapturingAgent();
        var agent = new ExternalAgentUserMemoryAgent(
            innerAgent,
            new StaticContextProvider("memory context"),
            ExternalAgentKind.Codex
        );

        await agent.DisposeAsync();
        await agent.DisposeAsync();

        Assert.Equal(1, innerAgent.DisposeCount);
    }

    private sealed class StaticContextProvider : AIContextProvider
    {
        private readonly string? _text;

        public StaticContextProvider(string? text)
        {
            _text = text;
        }

        public override IReadOnlyList<string> StateKeys => [];

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult(
                new AIContext { Messages = _text == null ? null : [new ChatMessage(ChatRole.User, _text)] }
            );
    }

    private sealed class CapturingAgent : AIAgent, IAsyncDisposable
    {
        public IReadOnlyList<ChatMessage> RequestMessages { get; private set; } = [];

        public int DisposeCount { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new CapturingSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new CapturingSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        )
        {
            RequestMessages = messages.ToList();
            return Task.FromResult(new AgentResponse { Messages = [new ChatMessage(ChatRole.Assistant, "answer")] });
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            RequestMessages = messages.ToList();
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, "answer");
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        private sealed class CapturingSession : AgentSession;
    }

    private sealed class RecordingChatHistoryProvider : ChatHistoryProvider
    {
        public List<HistoryCall> Calls { get; } = [];

        protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken)
        {
            Calls.Add(new HistoryCall(context.RequestMessages.ToList(), context.ResponseMessages?.ToList() ?? []));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record HistoryCall(
        IReadOnlyList<ChatMessage> RequestMessages,
        IReadOnlyList<ChatMessage> ResponseMessages
    );
}
