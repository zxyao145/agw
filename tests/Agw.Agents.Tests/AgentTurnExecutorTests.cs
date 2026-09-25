using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Summaries;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Data.Entities.Agents;
using Agw.Tools.Impl.ToolBlocks.Todo;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentTurnExecutorTests
{
    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex")]
    [InlineData("pi")]
    public async Task RunAsync_ExternalNativeResult_PreservesAuthorAndSkipsLegacySummary(string author)
    {
        // Arrange
        var summaryService = new RecordingSummaryService();
        var agent = CreateAgent(new StubChatClient("native answer") { ResultAuthor = author });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            Guid.CreateVersion7(),
            "context-1",
            sessionStateScope: null,
            agentType: AgentType.External,
            enableSummary: true,
            summaryModelProviderId: Guid.CreateVersion7(),
            summaryService: summaryService
        );

        // Act
        var output = await RunAsync(runtime, Input("request"));

        // Assert
        var result = Assert.Single(output, message => IsMessageType(message.AdditionalProperties, "result"));
        Assert.Equal(author, result.Author);
        Assert.Equal("native answer", Assert.IsType<AgwTextContent>(Assert.Single(result.Contents)).Content);
        Assert.Empty(summaryService.Calls);
        Assert.Empty(summaryService.StructuredCalls);
    }

    [Fact]
    public async Task RunAsync_SummaryEnabled_AppendsResultUsingOnlyCurrentTurnText()
    {
        var projectId = Guid.CreateVersion7();
        var modelProviderId = Guid.CreateVersion7();
        var summaryService = new RecordingSummaryService();
        var agent = CreateAgent(new StubChatClient("assistant response"));
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            projectId,
            "context-1",
            sessionStateScope: null,
            enableSummary: true,
            summaryModelProviderId: modelProviderId,
            summaryService: summaryService
        );

        var messages = await RunAsync(
            runtime,
            new AgwUserInput
            {
                Contents =
                [
                    new AgwTextContent { Content = "user request" },
                    new AgwUriContent(new Uri("https://example.com"), "text/html"),
                ],
            }
        );

        Assert.Equal(2, messages.Count);
        Assert.Equal("assistant response", Assert.IsType<AgwTextContent>(Assert.Single(messages[0].Contents)).Content);
        Assert.Equal("result", messages[1].AdditionalProperties!["type"]);

        var call = Assert.Single(summaryService.Calls);
        Assert.Equal(modelProviderId, call.ModelProviderId);
        Assert.Equal(projectId, call.ProjectId);
        Assert.Equal("context-1", call.ContextId);
        Assert.Null(call.CustomInstructions);
        Assert.Equal([ChatRole.User, ChatRole.Assistant], call.Messages.Select(message => message.Role));
        Assert.Equal(["user request", "assistant response"], call.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task RunAsync_SummaryEnabled_PreservesWhitespaceOnlyChunksForSummary()
    {
        var summaryService = new RecordingSummaryService();
        var agent = CreateAgent(new StubChatClient("assistant response", "assistant", " ", "response"));
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            Guid.CreateVersion7(),
            "context-1",
            sessionStateScope: null,
            enableSummary: true,
            summaryModelProviderId: Guid.CreateVersion7(),
            summaryService: summaryService
        );

        await RunAsync(runtime, Input("user request"));

        var call = Assert.Single(summaryService.Calls);
        Assert.Equal("assistant response", call.Messages.Single(message => message.Role == ChatRole.Assistant).Text);
    }

    [Fact]
    public async Task RunAsync_StructuredResult_UsesOnlyFinalAssistantResponse()
    {
        // Arrange
        var projectId = Guid.CreateVersion7();
        var summaryService = new RecordingSummaryService();
        var agent = new MultipleApprovalAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            projectId,
            "context-1",
            sessionStateScope: null,
            enableSummary: true,
            useStructuredResult: true,
            summaryService: summaryService
        );

        // Act
        await RunAsync(runtime, Input("run tools"), new RecordingApprovalHandler());

        // Assert
        var call = Assert.Single(summaryService.StructuredCalls);
        Assert.Equal("done", call.FinalText);
        Assert.Equal(projectId, call.ProjectId);
        Assert.Equal("context-1", call.ContextId);
        Assert.Empty(summaryService.Calls);
    }

    [Fact]
    public async Task RunAsync_StructuredResultWithoutFinalText_DoesNotAppendResult()
    {
        // Arrange
        var summaryService = new RecordingSummaryService();
        var agent = CreateAgent(new StubChatClient("   "));
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            Guid.CreateVersion7(),
            "context-1",
            sessionStateScope: null,
            enableSummary: true,
            summaryService: summaryService,
            useStructuredResult: true
        );

        // Act
        var messages = await RunAsync(runtime, Input("request"));

        // Assert
        Assert.DoesNotContain(messages, message => IsMessageType(message.AdditionalProperties, "result"));
        Assert.Empty(summaryService.StructuredCalls);
        Assert.Empty(summaryService.Calls);
    }

    [Fact]
    public async Task RunAsync_SummaryDisabled_DoesNotGenerateResult()
    {
        var summaryService = new RecordingSummaryService();
        var agent = CreateAgent(new StubChatClient("assistant response"));
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            Guid.CreateVersion7(),
            "context-1",
            sessionStateScope: null,
            enableSummary: false,
            useStructuredResult: true,
            summaryModelProviderId: Guid.CreateVersion7(),
            summaryService: summaryService
        );

        var messages = await RunAsync(runtime, Input("user request"));

        Assert.Single(messages);
        Assert.Empty(summaryService.Calls);
        Assert.Empty(summaryService.StructuredCalls);
    }

    [Fact]
    public async Task RunAsync_ConversationHandoff_PassesPreviousContentBeforeCurrentInput()
    {
        var chatClient = new StubChatClient("assistant response");
        var agent = CreateAgent(chatClient);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var agentId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            projectId,
            "context-1",
            new AgentSessionStateScope(conversationId, projectId, "context-1", agentId, generation: 0)
        );
        var handoff = new PreviousPlanHandoffProvider();

        await RunAsync(runtime, Input("current input"), handoffProvider: handoff);

        Assert.Equal((conversationId, AgentRuntimeType.Agent, agentId), handoff.LastRequest);
        var request = Assert.Single(chatClient.Requests);
        Assert.Equal(["previous plan", "current input"], request.Select(message => message.Text));
    }

    [Fact]
    public async Task RunAsync_TodoToolBlock_WithoutToolInvocation_DoesNotPersistStateSnapshot()
    {
        var projectId = Guid.CreateVersion7();
        var historyWriter = new RecordingConversationHistoryWriter();
        var agent = CreateTodoAgent(new StubChatClient("assistant response"));
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            projectId,
            "context-1",
            sessionStateScope: null,
            conversationHistoryWriter: historyWriter
        );

        var messages = await RunAsync(runtime, Input("user request"));

        Assert.DoesNotContain(
            messages,
            message => IsMessageType(message.AdditionalProperties, AgwMessageTypes.ToolTodoSnapshot)
        );
        Assert.Empty(historyWriter.Calls);
    }

    [Fact]
    public async Task RunAsync_ConsumerStopsEarly_PersistsCapturedToolMessages()
    {
        var historyWriter = new RecordingConversationHistoryWriter();
        var agent = new ToolMessageAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            Guid.CreateVersion7(),
            "context-1",
            sessionStateScope: null,
            conversationHistoryWriter: historyWriter
        );

        await RunAsync(runtime, Input("user request"), stopAfterFirst: true);

        var call = Assert.Single(historyWriter.Calls);
        var warning = Assert.Single(call.Messages);
        Assert.Equal(AgwMessageTypes.ToolWarning, warning.AdditionalProperties!["type"]?.ToString());
    }

    [Fact]
    public async Task RunAsync_MultipleApprovalRequests_RespondsToEveryRequest()
    {
        var agent = new MultipleApprovalAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            Guid.CreateVersion7(),
            "context-1",
            sessionStateScope: null
        );
        var approvalHandler = new RecordingApprovalHandler();

        await RunAsync(runtime, Input("run both tools"), approvalHandler);

        Assert.Equal(["approval-1", "approval-2"], approvalHandler.ProviderRequestIds);
        Assert.Equal(2, agent.ReceivedApprovalResponses);
    }

    [Fact]
    public async Task RunAsync_Completes_ReportsCompletedOutcome()
    {
        var agent = CreateAgent(new StubChatClient("assistant response"));
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            await agent.CreateSessionAsync(TestContext.Current.CancellationToken),
            Guid.CreateVersion7(),
            "context-1",
            sessionStateScope: null
        );
        var scope = ExecutionTestScopes.Scope();

        await RunAsync(runtime, Input("request"), scope: scope);

        Assert.Equal(TurnOutcomeStatus.Completed, scope.Outcome!.Status);
        Assert.Empty(scope.Outcome.PendingInteractions);
    }

    private static AgwUserInput Input(string text) => new() { Contents = [new AgwTextContent { Content = text }] };

    /// <summary>
    /// 在执行作用域内用一个 AgentTurnExecutor 执行 Turn，收集消息流。
    /// Runs one turn with an AgentTurnExecutor inside an execution scope and collects the message stream.
    /// </summary>
    private static async Task<List<AgwMessage>> RunAsync(
        AgentRuntime runtime,
        AgwUserInput input,
        IInteractionHandler? handler = null,
        IConversationHandoffProvider? handoffProvider = null,
        bool stopAfterFirst = false,
        Agw.Agents.Execution.Context.ExecutionScope? scope = null
    )
    {
        scope ??= ExecutionTestScopes.Scope();
        scope.BindInteractions(handler, null, handler?.Requests);
        var executor = new AgentTurnExecutor(null, null, handoffProvider, NullLogger<AgentTurnExecutor>.Instance);
        var messages = new List<AgwMessage>();
        await foreach (
            var message in scope.RunStreaming(
                executor.RunAsync(scope, runtime, new TurnInput(input), TestContext.Current.CancellationToken)
            )
        )
        {
            messages.Add(message);
            if (stopAfterFirst)
                break;
        }
        return messages;
    }

    private sealed class PreviousPlanHandoffProvider : IConversationHandoffProvider
    {
        public (Guid ConversationId, AgentRuntimeType Type, Guid TargetId)? LastRequest { get; private set; }

        public Task<ConversationHandoff> CreateAsync(
            Guid conversationId,
            AgentRuntimeType targetType,
            Guid targetId,
            CancellationToken cancellationToken = default
        )
        {
            LastRequest = (conversationId, targetType, targetId);
            return Task.FromResult(new ConversationHandoff([new ChatMessage(ChatRole.Assistant, "previous plan")], 1));
        }
    }

    private static AIAgent CreateAgent(IChatClient chatClient) =>
        new ChatClientAgent(chatClient, new ChatClientAgentOptions { Name = "test-agent" });

    private static AIAgent CreateTodoAgent(IChatClient chatClient) =>
        new TodoAgent(new ChatClientAgent(chatClient, new ChatClientAgentOptions { Name = "test-agent" }));

    private static bool IsMessageType(AdditionalPropertiesDictionary? properties, string expectedType) =>
        properties?.TryGetValue("type", out var type) == true
        && string.Equals(type?.ToString(), expectedType, StringComparison.Ordinal);

    private sealed class RecordingSummaryService : IAgentTurnSummaryService, IAgentStructuredResultService
    {
        public List<Call> Calls { get; } = [];
        public List<StructuredCall> StructuredCalls { get; } = [];

        public Task<ChatMessage> CreateResultAsync(
            Guid modelProviderId,
            IReadOnlyList<ChatMessage> sourceMessages,
            Guid projectId,
            string contextId,
            string? customInstructions,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add(new Call(modelProviderId, sourceMessages, projectId, contextId, customInstructions));
            return Task.FromResult(AgentTurnSummaryService.CreateResultMessage("turn summary"));
        }

        public Task<ChatMessage> CreateStructuredResultAsync(
            string finalText,
            Guid projectId,
            string contextId,
            CancellationToken cancellationToken = default
        )
        {
            StructuredCalls.Add(new StructuredCall(finalText, projectId, contextId));
            return Task.FromResult(AgentTurnSummaryService.CreateResultMessage(finalText, ResultFormat.Json));
        }
    }

    private sealed record Call(
        Guid ModelProviderId,
        IReadOnlyList<ChatMessage> Messages,
        Guid ProjectId,
        string ContextId,
        string? CustomInstructions
    );

    private sealed record StructuredCall(string FinalText, Guid ProjectId, string ContextId);

    private sealed class RecordingConversationHistoryWriter : IConversationHistoryWriter
    {
        public List<HistoryCall> Calls { get; } = [];

        public Task AppendAsync(
            Guid projectId,
            string contextId,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add(new HistoryCall(projectId, contextId, messages.ToList()));
            return Task.CompletedTask;
        }
    }

    private sealed record HistoryCall(Guid ProjectId, string ContextId, IReadOnlyList<ChatMessage> Messages);

    private sealed class TodoAgent : DelegatingAIAgent
    {
        private readonly AgwTodoProvider _agwTodoProvider = new();

        public TodoAgent(AIAgent innerAgent)
            : base(innerAgent) { }

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            base.GetService(serviceType, serviceKey) ?? _agwTodoProvider.GetService(serviceType, serviceKey);
    }

    private sealed class ToolMessageAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ToolMessageSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new ToolMessageSession());

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
            yield return new AgentResponseUpdate(ChatRole.System, [new TextContent(string.Empty)])
            {
                AuthorName = "tools",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["type"] = AgwMessageTypes.ToolWarning,
                    ["persistSeparately"] = true,
                },
            };
            yield return new AgentResponseUpdate(ChatRole.Assistant, "not consumed");
            await Task.CompletedTask;
        }

        private sealed class ToolMessageSession : AgentSession;
    }

    private sealed class RecordingApprovalHandler : IInteractionHandler
    {
        public List<string> ProviderRequestIds { get; } = [];

        public ValueTask<InteractionResolution> ResolveAsync(
            InteractionRequest request,
            CancellationToken cancellationToken
        )
        {
            ProviderRequestIds.Add(request.Source.ProviderRequestId!);
            return ValueTask.FromResult<InteractionResolution>(
                new InteractionResolution.Resolved(InteractionTestData.Decision(request, approved: true, text: null))
            );
        }
    }

    private sealed class MultipleApprovalAgent : AIAgent
    {
        public int ReceivedApprovalResponses { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new MultipleApprovalSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new MultipleApprovalSession());

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
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, CreateResponseContents(messages));
        }

        private List<AIContent> CreateResponseContents(IEnumerable<ChatMessage> messages)
        {
            ReceivedApprovalResponses = messages
                .SelectMany(message => message.Contents)
                .OfType<ToolApprovalResponseContent>()
                .Count();
            return ReceivedApprovalResponses > 0
                ? [new TextContent("done")]
                :
                [
                    new TextContent("before tool"),
                    CreateApproval("approval-1", "call-1"),
                    CreateApproval("approval-2", "call-2"),
                ];
        }

        private static ToolApprovalRequestContent CreateApproval(string requestId, string callId) =>
            new(requestId, new FunctionCallContent(callId, "run_shell", new Dictionary<string, object?>()));

        private sealed class MultipleApprovalSession : AgentSession;
    }

    private sealed class StubChatClient : IChatClient
    {
        private readonly string _responseText;
        private readonly string[] _streamingChunks;

        public StubChatClient(string responseText, params string[] streamingChunks)
        {
            _responseText = responseText;
            _streamingChunks = streamingChunks;
        }

        public List<List<ChatMessage>> Requests { get; } = [];
        public string? ResultAuthor { get; init; }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Requests.Add(messages.ToList());
            await Task.Yield();
            foreach (var chunk in _streamingChunks.Length == 0 ? [_responseText] : _streamingChunks)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk)
                {
                    AuthorName = ResultAuthor,
                    AdditionalProperties = ResultAuthor == null ? null : new() { ["type"] = "result" },
                };
            }
        }
    }
}
