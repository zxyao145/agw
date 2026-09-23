using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public class ClaudeCodeProviderSessionTrackingAgentTests
{
    [Fact]
    public async Task RunStreamingAsync_RestatedToolCall_ForwardsOneFunctionCall()
    {
        var inner = new ScriptedAgent([
            Update("msg-1", new TextReasoningContent("thinking")),
            Update("msg-1", new FunctionCallContent("call-1", "AskUserQuestion", Arguments("first"))),
            Update("msg-1", new FunctionCallContent("call-1", "AskUserQuestion", Arguments("second"))),
            Update("msg-1", new TextContent("done")),
            Update("msg-2", new FunctionCallContent("call-2", "Bash", Arguments("other"))),
        ]);
        var agent = new ClaudeCodeProviderSessionTrackingAgent(inner, onProviderSessionStartedAsync: null);

        var contents = new List<AIContent>();
        await foreach (
            var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "ask me")],
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            contents.AddRange(update.Contents);
        }

        var calls = contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(["call-1", "call-2"], calls.Select(call => call.CallId));
        Assert.Equal("first", calls[0].Arguments?["value"]);
        Assert.Equal(
            ["thinking", "done"],
            contents.Where(content => content is not FunctionCallContent).Select(GetText)
        );
    }

    [Fact]
    public async Task RunAsync_RestatedToolCall_ForwardsOneFunctionCall()
    {
        var inner = new ScriptedAgent([
            Update("msg-1", new FunctionCallContent("call-1", "AskUserQuestion", Arguments("first"))),
            Update("msg-1", new FunctionCallContent("call-1", "AskUserQuestion", Arguments("second"))),
        ]);
        var agent = new ClaudeCodeProviderSessionTrackingAgent(inner, onProviderSessionStartedAsync: null);

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "ask me")],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToList();
        var call = Assert.Single(calls);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("first", call.Arguments?["value"]);
    }

    private static Dictionary<string, object?> Arguments(string value) => new() { ["value"] = value };

    private static string GetText(AIContent content) =>
        content switch
        {
            TextContent text => text.Text,
            TextReasoningContent reasoning => reasoning.Text,
            _ => content.GetType().Name,
        };

    private static AgentResponseUpdate Update(string messageId, AIContent content) =>
        new(ChatRole.Assistant, [content]) { MessageId = messageId, AuthorName = "claude-code" };

    private sealed class ScriptedAgent : AIAgent
    {
        private readonly IReadOnlyList<AgentResponseUpdate> _updates;

        public ScriptedAgent(IReadOnlyList<AgentResponseUpdate> updates)
        {
            _updates = updates;
        }

        public override string? Name => "Scripted";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ScriptedSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new ScriptedSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new AgentResponse(
                    _updates
                        .GroupBy(update => update.MessageId)
                        .Select(group => new ChatMessage(
                            ChatRole.Assistant,
                            group.SelectMany(update => update.Contents).ToList()
                        )
                        {
                            MessageId = group.Key,
                            AuthorName = "claude-code",
                        })
                        .ToList()
                )
            );

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            foreach (var update in _updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }

        private sealed class ScriptedSession : AgentSession;
    }
}
