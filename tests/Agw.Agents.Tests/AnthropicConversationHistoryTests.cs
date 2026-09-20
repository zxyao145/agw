using System.Text.Json;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Tools;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed partial class AgentRequestContextAgentTests
{
    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate, false, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.Immediate, false, "")]
    [InlineData(ConversationHistoryWriteMode.Immediate, true, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.Immediate, true, "")]
    [InlineData(ConversationHistoryWriteMode.Interval, false, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.Interval, false, "")]
    [InlineData(ConversationHistoryWriteMode.Interval, true, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.Interval, true, "")]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, false, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, false, "")]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, true, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, true, "")]
    public async Task AnthropicHistory_ToolLoopAndSessionReload_PreserveThinking(
        ConversationHistoryWriteMode mode,
        bool streaming,
        string thinkingText
    )
    {
        // Arrange: exercise the real SDK, persistence and compaction pipeline over a fake HTTP transport.
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync(mode);
        await using var model = new AnthropicReasoningChatClientTests.ClientFixture(
            "https://api.deepseek.com/anthropic"
        );
        model.Handler.RequireThinking = true;
        model.Handler.ReturnToolCall = true;
        model.Handler.ThinkingText = thinkingText;
        await using var capabilities = CreateCapabilities([AIFunctionFactory.Create(() => "result", "lookup")]);
        var definition = new ResolvedAgentDefinition
        {
            Id = "anthropic-thinking-agent",
            Name = "Thinking agent",
            ModelId = "thinking-model",
            OpenTelemetrySourceName = "test",
            ChatHistoryProvider = fixture.Provider,
            CompactionProvider = new CompactionProvider(new ContextWindowCompactionStrategy(100_000, 10_000)),
        };
        var agent = CreateAgent(
            model.Client.AsAgwAgent(definition, capabilities, NullLoggerFactory.Instance, fixture.Services),
            fixture.Provider,
            memoryText: null
        );
        var token = TestContext.Current.CancellationToken;
        var session = await InitializeHistorySessionAsync(agent, fixture);
        await fixture.Provider.AppendAsync(
            fixture.ProjectId,
            "streaming",
            [new(ChatRole.User, "old question"), new(ChatRole.Assistant, "old answer")],
            token
        );

        // Act
        await RunAsync("question");
        session = await agent.DeserializeSessionAsync(
            await agent.SerializeSessionAsync(session, cancellationToken: token),
            cancellationToken: token
        );
        await RunAsync("next question");

        // Assert: both the immediate tool continuation and persisted replay carry the original block.
        Assert.Equal(3, model.Handler.Requests.Count);
        foreach (var request in model.Handler.Requests.Skip(1))
        {
            var blocks = request
                .GetProperty("messages")
                .EnumerateArray()
                .SelectMany(message => message.GetProperty("content").EnumerateArray())
                .ToList();
            var thinking = Assert.Single(
                blocks,
                block =>
                    block.GetProperty("type").GetString() == "thinking"
                    && block.GetProperty("thinking").GetString() == thinkingText
                    && block.GetProperty("signature").GetString() == "original-signature"
            );
            Assert.Equal("original-signature", thinking.GetProperty("signature").GetString());
            Assert.Single(blocks, block => block.GetProperty("type").GetString() == "tool_use");
            Assert.Single(blocks, block => block.GetProperty("type").GetString() == "tool_result");
        }
        var history = (await fixture.ReadAsync())
            .Select(row =>
                JsonSerializer.Deserialize<ChatMessage>(
                    row.ConversationPayload!,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                )!
            )
            .ToList();
        Assert.Empty(
            Assert.Single(history, message => message.Text == "old answer").Contents.OfType<TextReasoningContent>()
        );
        var reasoning = Assert.Single(
            history.SelectMany(message => message.Contents).OfType<TextReasoningContent>(),
            content => content.Text == thinkingText
        );
        Assert.Equal("original-signature", reasoning.ProtectedData);

        async Task RunAsync(string text)
        {
            await using var scope = ConversationHistoryPersistenceContext.BeginScope(
                fixture.Provider,
                fixture.ProjectId,
                "streaming",
                0,
                token
            );
            if (streaming)
            {
                await foreach (
                    var _ in agent.RunStreamingAsync(
                        [new ChatMessage(ChatRole.User, text)],
                        session,
                        cancellationToken: token
                    )
                ) { }
            }
            else
                await agent.RunAsync([new ChatMessage(ChatRole.User, text)], session, cancellationToken: token);
        }
    }
}
