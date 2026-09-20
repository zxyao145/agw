using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Summaries;
using Agw.Shared.Data.Entities.Agents;
using Agw.Tools.Impl.ToolBlocks.Todo;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentRuntimeSummaryTests
{
    [Theory]
    [InlineData("claude-code", false)]
    [InlineData("claude-code", true)]
    [InlineData("codex", false)]
    [InlineData("codex", true)]
    [InlineData("pi", false)]
    [InlineData("pi", true)]
    public async Task Execute_ExternalNativeResult_PreservesAuthorAndSkipsLegacySummary(string author, bool streaming)
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
        var executor = new AgentTurnExecutor(null!, null!, null!);
        var input = new AgwUserInput { Contents = [new AgwTextContent { Content = "request" }] };
        var output = new List<AgwMessage>();

        // Act
        if (streaming)
        {
            await foreach (
                var message in executor.ExecuteStreamingAsync(runtime, input, TestContext.Current.CancellationToken)
            )
            {
                output.Add(message);
            }
        }
        else
        {
            output.AddRange(await executor.ExecuteAsync(runtime, input, TestContext.Current.CancellationToken));
        }

        // Assert
        var result = Assert.Single(output, message => IsMessageType(message.AdditionalProperties, "result"));
        Assert.Equal(author, result.Author);
        Assert.Equal("native answer", Assert.IsType<AgwTextContent>(Assert.Single(result.Contents)).Content);
        Assert.Empty(summaryService.Calls);
        Assert.Empty(summaryService.StructuredCalls);
    }

    [Fact]
    public async Task ExecuteAsync_SummaryEnabled_AppendsResultUsingOnlyCurrentTurnText()
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

        var messages = await runtime.ExecuteAsync(
            new AgwUserInput
            {
                Contents =
                [
                    new AgwTextContent { Content = "user request" },
                    new AgwUriContent(new Uri("https://example.com"), "text/html"),
                ],
            },
            TestContext.Current.CancellationToken
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
    public async Task ExecuteStreamingAsync_SummaryEnabled_YieldsResultAfterAssistantOutput()
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
            enableSummary: true,
            summaryModelProviderId: Guid.CreateVersion7(),
            summaryService: summaryService
        );
        var messages = new List<AgwMessage>();

        await foreach (
            var message in runtime.ExecuteStreamingAsync(
                new AgwUserInput { Contents = [new AgwTextContent { Content = "user request" }] },
                TestContext.Current.CancellationToken
            )
        )
        {
            messages.Add(message);
        }

        Assert.Equal(2, messages.Count);
        Assert.Equal("assistant response", Assert.IsType<AgwTextContent>(Assert.Single(messages[0].Contents)).Content);
        Assert.Equal("result", messages[1].AdditionalProperties!["type"]);
        Assert.Single(summaryService.Calls);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_SummaryEnabled_PreservesWhitespaceOnlyChunksForSummary()
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

        await foreach (
            var _ in runtime.ExecuteStreamingAsync(
                new AgwUserInput { Contents = [new AgwTextContent { Content = "user request" }] },
                TestContext.Current.CancellationToken
            )
        ) { }

        var call = Assert.Single(summaryService.Calls);
        Assert.Equal("assistant response", call.Messages.Single(message => message.Role == ChatRole.Assistant).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_StructuredResult_UsesOnlyFinalAssistantResponse(bool streaming)
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
        var input = new AgwUserInput { Contents = [new AgwTextContent { Content = "run tools" }] };
        var approvalHandler = new RecordingApprovalHandler();

        // Act
        if (streaming)
        {
            await foreach (
                var _ in runtime.ExecuteStreamingAsync(input, approvalHandler, TestContext.Current.CancellationToken)
            ) { }
        }
        else
        {
            await runtime.ExecuteAsync(input, approvalHandler, TestContext.Current.CancellationToken);
        }

        // Assert
        var call = Assert.Single(summaryService.StructuredCalls);
        Assert.Equal("done", call.FinalText);
        Assert.Equal(projectId, call.ProjectId);
        Assert.Equal("context-1", call.ContextId);
        Assert.Empty(summaryService.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_StructuredResultWithoutFinalText_DoesNotAppendResult()
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
        var messages = await runtime.ExecuteAsync(
            new AgwUserInput { Contents = [new AgwTextContent { Content = "request" }] },
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.DoesNotContain(messages, message => IsMessageType(message.AdditionalProperties, "result"));
        Assert.Empty(summaryService.StructuredCalls);
        Assert.Empty(summaryService.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_SummaryDisabled_DoesNotGenerateResult()
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

        var messages = await runtime.ExecuteAsync(
            new AgwUserInput { Contents = [new AgwTextContent { Content = "user request" }] },
            TestContext.Current.CancellationToken
        );

        Assert.Single(messages);
        Assert.Empty(summaryService.Calls);
        Assert.Empty(summaryService.StructuredCalls);
    }

    [Fact]
    public async Task ExecuteAsync_RequestMessageList_PassesHandoffBeforeCurrentInput()
    {
        var chatClient = new StubChatClient("assistant response");
        var agent = CreateAgent(chatClient);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            Guid.CreateVersion7(),
            "context-1",
            sessionStateScope: null
        );
        var summaryInput = new AgwUserInput { Contents = [new AgwTextContent { Content = "current input" }] };

        await runtime.ExecuteAsync(
            [new ChatMessage(ChatRole.Assistant, "previous plan"), AgwMessageUtil.CreateUserChatMessage(summaryInput)],
            summaryInput,
            approvalHandler: null,
            TestContext.Current.CancellationToken
        );

        var request = Assert.Single(chatClient.Requests);
        Assert.Equal(["previous plan", "current input"], request.Select(message => message.Text));
    }

    [Fact]
    public async Task ExecuteStreamingAsync_TodoToolBlock_WithoutToolInvocation_DoesNotPersistStateSnapshot()
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

        var messages = new List<AgwMessage>();
        await foreach (
            var message in runtime.ExecuteStreamingAsync(
                new AgwUserInput { Contents = [new AgwTextContent { Content = "user request" }] },
                TestContext.Current.CancellationToken
            )
        )
        {
            messages.Add(message);
        }

        Assert.DoesNotContain(
            messages,
            message => IsMessageType(message.AdditionalProperties, ToolMessageTypes.TodoSnapshot)
        );
        Assert.Empty(historyWriter.Calls);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_ConsumerStopsEarly_PersistsCapturedToolMessages()
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

        await foreach (
            var _ in runtime.ExecuteStreamingAsync(
                new AgwUserInput { Contents = [new AgwTextContent { Content = "user request" }] },
                TestContext.Current.CancellationToken
            )
        )
        {
            break;
        }

        var call = Assert.Single(historyWriter.Calls);
        var warning = Assert.Single(call.Messages);
        Assert.Equal(ToolMessageTypes.Warning, warning.AdditionalProperties!["type"]?.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_MultipleApprovalRequests_RespondsToEveryRequest(bool streaming)
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
        var input = new AgwUserInput { Contents = [new AgwTextContent { Content = "run both tools" }] };

        if (streaming)
        {
            await foreach (
                var _ in runtime.ExecuteStreamingAsync(input, approvalHandler, TestContext.Current.CancellationToken)
            ) { }
        }
        else
        {
            await runtime.ExecuteAsync(input, approvalHandler, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["approval-1", "approval-2"], approvalHandler.ProviderRequestIds);
        Assert.Equal(2, agent.ReceivedApprovalResponses);
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
                    ["type"] = ToolMessageTypes.Warning,
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
        )
        {
            var contents = CreateResponseContents(messages);
            return Task.FromResult(new AgentResponse([new ChatMessage(ChatRole.Assistant, contents)]));
        }

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

    private sealed class StubChatClient(string responseText, params string[] streamingChunks) : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];
        public string? ResultAuthor { get; init; }

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
            return Task.FromResult(
                new ChatResponse([
                    new ChatMessage(ChatRole.Assistant, responseText)
                    {
                        AuthorName = ResultAuthor,
                        AdditionalProperties = ResultAuthor == null ? null : new() { ["type"] = "result" },
                    },
                ])
            );
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Requests.Add(messages.ToList());
            await Task.Yield();
            foreach (var chunk in streamingChunks.Length == 0 ? [responseText] : streamingChunks)
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
