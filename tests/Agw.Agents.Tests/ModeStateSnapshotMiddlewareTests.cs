using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Shared.Contracts.Agents;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class ModeStateSnapshotMiddlewareTests
{
    [Fact]
    public async Task RunStreamingAsync_ModeSetCompleted_EmitsCurrentModeAfterResult()
    {
        var modeProvider = new AgentModeProvider(
            new AgentModeProviderOptions { DefaultMode = "plan" });
        using var providerResource = modeProvider;
        var session = new TestAgentSession();
        await modeProvider.SetModeAsync(
            session,
            "execute",
            TestContext.Current.CancellationToken);
        var middleware = new ModeStateSnapshotMiddleware(modeProvider);
        var updates = new List<AgentResponseUpdate>();

        await foreach (var update in middleware.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, "switch mode")],
                           session,
                           options: null,
                           new FunctionTranscriptAgent("Mode changed to \"execute\"."),
                           TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Equal(3, updates.Count);
        Assert.IsType<FunctionResultContent>(Assert.Single(updates[1].Contents));
        AssertModeSnapshot(updates[2], "execute");
    }

    [Fact]
    public async Task RunAsync_ModeSetCancelled_EmitsUnchangedModeAfterResult()
    {
        var modeProvider = new AgentModeProvider(
            new AgentModeProviderOptions { DefaultMode = "plan" });
        using var providerResource = modeProvider;
        var session = new TestAgentSession();
        var middleware = new ModeStateSnapshotMiddleware(modeProvider);

        var response = await middleware.RunAsync(
            [new ChatMessage(ChatRole.User, "switch mode")],
            session,
            options: null,
            new FunctionTranscriptAgent("Mode change was cancelled by the user."),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, response.Messages.Count);
        Assert.IsType<FunctionResultContent>(Assert.Single(response.Messages[1].Contents));
        AssertModeSnapshot(ToolStateSnapshots.ToUpdate(response.Messages[2]), "plan");
    }

    [Fact]
    public async Task CreateAsync_ModeToolCompleted_DoesNotCreateDuplicateTurnSnapshot()
    {
        var agent = new FunctionTranscriptAgent("Mode changed to \"execute\".");
        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "switch mode")],
            cancellationToken: TestContext.Current.CancellationToken);

        var snapshots = await ToolStateSnapshots.CreateAsync(
            agent,
            new TestAgentSession(),
            response.Messages,
            TestContext.Current.CancellationToken);

        Assert.Empty(snapshots);
    }

    private static void AssertModeSnapshot(AgentResponseUpdate update, string mode)
    {
        Assert.Equal(ToolMessageTypes.ModeStatus, update.AdditionalProperties!["type"]);
        Assert.Equal("mode_set", update.AdditionalProperties["toolName"]);
        Assert.Equal("mode-call-1", update.AdditionalProperties["callId"]);
        Assert.Equal(mode, update.AdditionalProperties["mode"]);
        Assert.True(ToolStateSnapshots.RequiresSeparatePersistence(update));
    }

    private sealed class FunctionTranscriptAgent : AIAgent
    {
        private readonly string _result;

        public FunctionTranscriptAgent(string result)
        {
            _result = result;
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentResponse(
                [CreateFunctionCallMessage(), CreateFunctionResultMessage()]));

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return ToolStateSnapshots.ToUpdate(CreateFunctionCallMessage());
            yield return ToolStateSnapshots.ToUpdate(CreateFunctionResultMessage());
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

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
                        "mode-call-1",
                        "mode_set",
                        new Dictionary<string, object?> { ["mode"] = "execute" })
                ]);

        private ChatMessage CreateFunctionResultMessage() =>
            new(
                ChatRole.Tool,
                [new FunctionResultContent("mode-call-1", _result)]);
    }

    private sealed class TestAgentSession : AgentSession;
}
