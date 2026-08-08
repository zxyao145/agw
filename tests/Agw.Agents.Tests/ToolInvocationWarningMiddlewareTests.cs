using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Shared.Contracts.Agents;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class ToolInvocationWarningMiddlewareTests
{
    private const string Warning =
        "Hosted web search is not supported by this provider; using local search.";

    [Fact]
    public async Task RunStreamingAsync_FunctionRequestedButNotCompleted_DoesNotEmitWarning()
    {
        var middleware = CreateMiddleware();
        var updates = new List<AgentResponseUpdate>();

        await foreach (var update in middleware.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, "search")],
                           session: null,
                           options: null,
                           new FunctionTranscriptAgent(includeResult: false),
                           TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.DoesNotContain(updates, IsWarning);
    }

    [Fact]
    public async Task RunStreamingAsync_FunctionCompleted_EmitsWarningBeforeResult()
    {
        var middleware = CreateMiddleware();
        var updates = new List<AgentResponseUpdate>();

        await foreach (var update in middleware.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, "search")],
                           session: null,
                           options: null,
                           new FunctionTranscriptAgent(includeResult: true),
                           TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Equal(3, updates.Count);
        Assert.True(IsWarning(updates[1]));
        Assert.Equal(Warning, updates[1].Text);
        Assert.True(ToolStateSnapshots.RequiresSeparatePersistence(updates[1]));
        Assert.IsType<FunctionResultContent>(Assert.Single(updates[2].Contents));
    }

    [Fact]
    public async Task RunAsync_FunctionCompleted_InsertsWarningBeforeResult()
    {
        var middleware = CreateMiddleware();

        var response = await middleware.RunAsync(
            [new ChatMessage(ChatRole.User, "search")],
            session: null,
            options: null,
            new FunctionTranscriptAgent(includeResult: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, response.Messages.Count);
        Assert.True(IsWarning(response.Messages[1]));
        Assert.Equal(Warning, response.Messages[1].Text);
        Assert.True(ToolStateSnapshots.RequiresSeparatePersistence(response.Messages[1]));
        Assert.IsType<FunctionResultContent>(
            Assert.Single(response.Messages[2].Contents));
    }

    private static ToolInvocationWarningMiddleware CreateMiddleware() =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["web_search"] = Warning
        });

    private static bool IsWarning(AgentResponseUpdate update) =>
        update.AdditionalProperties?.TryGetValue("type", out var type) == true &&
        string.Equals(type?.ToString(), ToolMessageTypes.Warning, StringComparison.Ordinal);

    private static bool IsWarning(ChatMessage message) =>
        message.AdditionalProperties?.TryGetValue("type", out var type) == true &&
        string.Equals(type?.ToString(), ToolMessageTypes.Warning, StringComparison.Ordinal);

    private sealed class FunctionTranscriptAgent : AIAgent
    {
        private readonly bool _includeResult;

        public FunctionTranscriptAgent(bool includeResult)
        {
            _includeResult = includeResult;
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            var responseMessages = new List<ChatMessage>
            {
                CreateFunctionCallMessage()
            };
            if (_includeResult)
            {
                responseMessages.Add(CreateFunctionResultMessage());
            }

            return Task.FromResult(new AgentResponse(responseMessages));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return ToolStateSnapshots.ToUpdate(CreateFunctionCallMessage());
            if (_includeResult)
            {
                yield return ToolStateSnapshots.ToUpdate(CreateFunctionResultMessage());
            }
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(
                new { },
                jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        private static ChatMessage CreateFunctionCallMessage() =>
            new(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call-1",
                        "web_search",
                        new Dictionary<string, object?> { ["query"] = "agw" })
                ]);

        private static ChatMessage CreateFunctionResultMessage() =>
            new(
                ChatRole.Tool,
                [new FunctionResultContent("call-1", new { results = Array.Empty<object>() })]);

        private sealed class TestAgentSession : AgentSession;
    }
}
