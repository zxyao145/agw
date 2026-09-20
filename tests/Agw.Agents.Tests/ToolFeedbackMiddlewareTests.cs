using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Middleware.ToolFeedback;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Tools.Impl.ToolBlocks.Todo;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class ToolFeedbackMiddlewareTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_AllFeedbackEnabled_PreservesOrderAndDeduplicationAcrossRuns(bool streaming)
    {
        using var todos = new AgwTodoProvider();
        using var mode = new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = "plan" });
        var middleware = CreateMiddleware(todos, mode);
        var session = new TestSession();
        var calls = new ChatMessage(ChatRole.Assistant, [Call("mode", "mode_set"), Call("todo", "todos_add")]);
        var results = new ChatMessage(ChatRole.Tool, [Result("mode"), Result("todo")]);
        var agent = new TranscriptAgent([calls, results, results]);

        for (var run = 0; run < 2; run++)
        {
            var output = await RunAsync(middleware, agent, session, streaming);

            string[] expected =
            [
                "setup",
                "message",
                "mode warning",
                "todo warning",
                "message",
                ToolMessageTypes.TodoSnapshot,
                ToolMessageTypes.ModeStatus,
                "message",
            ];
            Assert.Equal(expected, output.Select(Describe));
            Assert.False(ToolStateSnapshots.RequiresSeparatePersistence(output[0]));
            Assert.True(ToolStateSnapshots.RequiresSeparatePersistence(output[2]));
            Assert.True(ToolStateSnapshots.RequiresSeparatePersistence(output[3]));
            Assert.Equal("mode", output[2].AdditionalProperties!["callId"]);
            Assert.Equal("todo", output[3].AdditionalProperties!["callId"]);
            Assert.Empty(ConversationHistoryPrelude.Take(session));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_FailedResults_EmitsWarningsWithoutSnapshots(bool streaming)
    {
        using var todos = new AgwTodoProvider();
        using var mode = new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = "plan" });
        var middleware = CreateMiddleware(todos, mode);
        var agent = new TranscriptAgent([
            new(ChatRole.Assistant, [Call("mode", "mode_set"), Call("todo", "todos_add")]),
            new(
                ChatRole.Tool,
                [
                    new FunctionResultContent("mode", "failed") { Exception = new InvalidOperationException("failed") },
                    new FunctionResultContent("todo", new ToolExecutionErrorResult(true, 500_0001, "failed")),
                ]
            ),
        ]);

        var output = await RunAsync(middleware, agent, new TestSession(), streaming);

        Assert.Equal(["setup", "message", "mode warning", "todo warning", "message"], output.Select(Describe));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RunStreamingAsync_ApprovalInput_EmitsSnapshotsWithoutInvocationWarnings(int approvalKind)
    {
        using var todos = new AgwTodoProvider();
        using var mode = new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = "plan" });
        var middleware = new ToolFeedbackMiddleware(todos, mode, [], Warnings());
        AIContent Approval(string id, string toolName)
        {
            var request = new ToolApprovalRequestContent($"approval-{id}", Call(id, toolName));
            return approvalKind switch
            {
                0 => request,
                1 => request.CreateResponse(approved: true),
                _ => request.CreateAlwaysApproveToolResponse(),
            };
        }
        var input = new ChatMessage(ChatRole.User, [Approval("mode", "mode_set"), Approval("todo", "todos_add")]);
        var agent = new TranscriptAgent([new(ChatRole.Tool, [Result("mode"), Result("todo")])]);

        var output = await RunAsync(middleware, agent, new TestSession(), true, [input]);

        Assert.Equal(["message", ToolMessageTypes.TodoSnapshot, ToolMessageTypes.ModeStatus], output.Select(Describe));
    }

    [Fact]
    public async Task RunStreamingAsync_InformationalCall_EmitsWarningWithoutSnapshot()
    {
        using var mode = new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = "plan" });
        var middleware = new ToolFeedbackMiddleware(null, mode, [], Warnings());
        var call = Call("mode", "mode_set");
        call.InformationalOnly = true;
        var agent = new TranscriptAgent([new(ChatRole.Assistant, [call]), new(ChatRole.Tool, [Result("mode")])]);

        var output = await RunAsync(middleware, agent, new TestSession(), true);

        Assert.Equal(["message", "mode warning", "message"], output.Select(Describe));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_NoSession_StillEmitsWarnings(bool streaming)
    {
        using var todos = new AgwTodoProvider();
        using var mode = new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = "plan" });
        var middleware = CreateMiddleware(todos, mode);
        var agent = new TranscriptAgent([
            new(ChatRole.Assistant, [Call("mode", "mode_set")]),
            new(ChatRole.Tool, [Result("mode")]),
        ]);

        var output = await RunAsync(middleware, agent, null, streaming);

        Assert.Equal(["setup", "message", "mode warning", "message"], output.Select(Describe));
    }

    [Fact]
    public async Task RunStreamingAsync_InterleavedSessions_KeepCallTrackingIndependent()
    {
        using var mode = new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = "plan" });
        var middleware = new ToolFeedbackMiddleware(null, mode, [], Warnings());
        var firstSession = new TestSession();
        var secondSession = new TestSession();
        await mode.SetModeAsync(secondSession, "execute", TestContext.Current.CancellationToken);
        var agent = new TranscriptAgent([
            new(ChatRole.Assistant, [Call("same-id", "mode_set")]),
            new(ChatRole.Tool, [Result("same-id")]),
        ]);
        await using var first = middleware
            .RunStreamingAsync([], firstSession, null, agent, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var second = middleware
            .RunStreamingAsync([], secondSession, null, agent, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var firstOutput = new List<AgentResponseUpdate>();
        var secondOutput = new List<AgentResponseUpdate>();
        while (await first.MoveNextAsync())
        {
            firstOutput.Add(first.Current);
            Assert.True(await second.MoveNextAsync());
            secondOutput.Add(second.Current);
        }

        Assert.False(await second.MoveNextAsync());
        Assert.Equal(["message", "mode warning", "message", ToolMessageTypes.ModeStatus], firstOutput.Select(Describe));
        Assert.Equal(firstOutput.Select(Describe), secondOutput.Select(Describe));
        Assert.Equal("plan", firstOutput[^1].AdditionalProperties!["mode"]);
        Assert.Equal("execute", secondOutput[^1].AdditionalProperties!["mode"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_InnerFailure_ClearsPrelude(bool streaming)
    {
        var middleware = new ToolFeedbackMiddleware(null, null, ["setup"], Warnings());
        var session = new TestSession();
        var agent = new TranscriptAgent([], fail: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(middleware, agent, session, streaming));

        Assert.Empty(ConversationHistoryPrelude.Take(session));
    }

    [Fact]
    public async Task RunStreamingAsync_DisposedAfterPrelude_ClearsPrelude()
    {
        var middleware = new ToolFeedbackMiddleware(null, null, ["setup"], Warnings());
        var session = new TestSession();
        var enumerator = middleware
            .RunStreamingAsync([], session, null, new TranscriptAgent([]), TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("setup", enumerator.Current.Text);
        await enumerator.DisposeAsync();

        Assert.Empty(ConversationHistoryPrelude.Take(session));
    }

    private static ToolFeedbackMiddleware CreateMiddleware(AgwTodoProvider todos, AgentModeProvider mode) =>
        new(todos, mode, ["setup"], Warnings());

    private static Dictionary<string, string> Warnings() =>
        new() { ["mode_set"] = "mode warning", ["todos_add"] = "todo warning" };

    private static FunctionCallContent Call(string id, string name) => new(id, name, new Dictionary<string, object?>());

    private static FunctionResultContent Result(string id) => new(id, "success");

    private static string Describe(AgentResponseUpdate update) =>
        update.AdditionalProperties?.GetValueOrDefault("type")?.ToString() is { } type
            ? type == "tool-warning"
                ? update.Text
                : type
            : "message";

    private static async Task<List<AgentResponseUpdate>> RunAsync(
        ToolFeedbackMiddleware middleware,
        AIAgent agent,
        AgentSession? session,
        bool streaming,
        IEnumerable<ChatMessage>? input = null
    )
    {
        input ??= [new(ChatRole.User, "run")];
        if (!streaming)
        {
            var response = await middleware.RunAsync(
                input,
                session,
                null,
                agent,
                TestContext.Current.CancellationToken
            );
            return response.Messages.Select(ToolStateSnapshots.ToUpdate).ToList();
        }
        var updates = new List<AgentResponseUpdate>();
        await foreach (
            var update in middleware.RunStreamingAsync(
                input,
                session,
                null,
                agent,
                TestContext.Current.CancellationToken
            )
        )
            updates.Add(update);
        return updates;
    }

    private sealed class TestSession : AgentSession;

    private sealed class TranscriptAgent : AIAgent
    {
        private readonly IReadOnlyList<ChatMessage> _messages;
        private readonly bool _fail;

        public TranscriptAgent(IReadOnlyList<ChatMessage> messages, bool fail = false)
        {
            _messages = messages;
            _fail = fail;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

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
        )
        {
            if (_fail)
                throw new InvalidOperationException("Agent failed.");
            return Task.FromResult(new AgentResponse(_messages.ToList()));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            if (_fail)
                throw new InvalidOperationException("Agent failed.");
            foreach (var message in _messages)
            {
                var update = ToolStateSnapshots.ToUpdate(message);
                update.MessageId = Guid.CreateVersion7().ToString("N");
                yield return update;
            }
            await Task.CompletedTask;
        }
    }
}
