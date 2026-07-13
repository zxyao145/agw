using System.Runtime.CompilerServices;

using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Summaries;
using Agw.Shared.AgwMsgVm;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentRuntimeSummaryTests
{
    [Fact]
    public async Task ExecuteAsync_SummaryEnabled_AppendsResultUsingOnlyCurrentTurnText()
    {
        var projectId = Guid.NewGuid();
        var modelProviderId = Guid.NewGuid();
        var summaryService = new RecordingSummaryService();
        var agent = CreateAgent(new StubChatClient("assistant response"));
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            projectId,
            "context-1",
            "session-1",
            enableSummary: true,
            summaryModelProviderId: modelProviderId,
            summaryService);

        var messages = await runtime.ExecuteAsync(
            new AgwUserInput
            {
                Contents =
                [
                    new AgwTextContent { Content = "user request" },
                    new AgwUriContent(new Uri("https://example.com"), "text/html")
                ]
            },
            TestContext.Current.CancellationToken);

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
            Guid.NewGuid(),
            "context-1",
            "session-1",
            enableSummary: true,
            summaryModelProviderId: Guid.NewGuid(),
            summaryService);
        var messages = new List<AgwMessage>();

        await foreach (var message in runtime.ExecuteStreamingAsync(
            new AgwUserInput { Contents = [new AgwTextContent { Content = "user request" }] },
            TestContext.Current.CancellationToken))
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
            Guid.NewGuid(),
            "context-1",
            "session-1",
            enableSummary: true,
            summaryModelProviderId: Guid.NewGuid(),
            summaryService);

        await foreach (var _ in runtime.ExecuteStreamingAsync(
            new AgwUserInput { Contents = [new AgwTextContent { Content = "user request" }] },
            TestContext.Current.CancellationToken))
        {
        }

        var call = Assert.Single(summaryService.Calls);
        Assert.Equal("assistant response", call.Messages.Single(message => message.Role == ChatRole.Assistant).Text);
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
            Guid.NewGuid(),
            "context-1",
            "session-1",
            enableSummary: false,
            summaryModelProviderId: Guid.NewGuid(),
            summaryService);

        var messages = await runtime.ExecuteAsync(
            new AgwUserInput { Contents = [new AgwTextContent { Content = "user request" }] },
            TestContext.Current.CancellationToken);

        Assert.Single(messages);
        Assert.Empty(summaryService.Calls);
    }

    private static AIAgent CreateAgent(IChatClient chatClient) =>
        new ChatClientAgent(chatClient, new ChatClientAgentOptions { Name = "test-agent" });

    private sealed class RecordingSummaryService : IAgentTurnSummaryService
    {
        public List<Call> Calls { get; } = [];

        public Task<ChatMessage> CreateResultAsync(
            Guid modelProviderId,
            IReadOnlyList<ChatMessage> sourceMessages,
            Guid projectId,
            string contextId,
            string? customInstructions,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(
                modelProviderId,
                sourceMessages,
                projectId,
                contextId,
                customInstructions));
            return Task.FromResult(AgentTurnSummaryService.CreateResultMessage("turn summary"));
        }
    }

    private sealed record Call(
        Guid ModelProviderId,
        IReadOnlyList<ChatMessage> Messages,
        Guid ProjectId,
        string ContextId,
        string? CustomInstructions);

    private sealed class StubChatClient(string responseText, params string[] streamingChunks) : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var chunk in streamingChunks.Length == 0 ? [responseText] : streamingChunks)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }
    }
}
