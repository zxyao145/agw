using System.Text.Json;
using Agw.Projects.Application.History;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

/// <summary>
/// Result 的唯一判定与 External Engine 原生 Result 的归一形态。
/// The single Result classification and the normalized shape of External Engine native Results.
/// </summary>
public sealed class ResultNormalizationTests : IDisposable
{
    private readonly IDisposable _userScope = HistoryTestFixture.EnterUser();

    public void Dispose() => _userScope.Dispose();

    [Theory]
    [InlineData(EngineKind.ClaudeCode, "claude-code", false)]
    [InlineData(EngineKind.Codex, "codex", false)]
    [InlineData(EngineKind.Pi, "pi", false)]
    [InlineData(EngineKind.ClaudeCode, "claude-code", true)]
    [InlineData(EngineKind.Codex, "codex", true)]
    [InlineData(EngineKind.Pi, "pi", true)]
    public async Task RecordAsync_NativeResult_NormalizesTypePurposeFormatAndSource(
        EngineKind engine,
        string author,
        bool contentLevelMarker
    )
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var history = fixture.CreateProvider(engine);
        var agent = new HistoryNotifyingAgent(history);
        var session = await fixture.CreateSessionAsync(agent);
        var recording = history.Begin(agent, session);
        var token = TestContext.Current.CancellationToken;
        var answer = new AgentResponseUpdate(ChatRole.Assistant, "final answer")
        {
            MessageId = "answer-1",
            AuthorName = author,
        };
        var result = contentLevelMarker
            ? new AgentResponseUpdate(
                ChatRole.Assistant,
                [new TextContent("final answer") { AdditionalProperties = new() { ["type"] = "result" } }]
            )
            {
                MessageId = "result-1",
                AuthorName = author,
            }
            : new AgentResponseUpdate(ChatRole.Assistant, "final answer")
            {
                MessageId = "result-1",
                AuthorName = author,
                AdditionalProperties = new() { ["type"] = "result", ["subtype"] = "success" },
            };

        await recording.RecordAsync(answer, token);
        await recording.RecordAsync(result, token);
        await recording.FinishAsync(completed: true, token);
        history.End(session);

        var rows = await fixture.ReadAsync();
        var answerRow = Assert.Single(rows, row => !AgwMessageClassifier.IsResult(row.ToChatMessage()!));
        var resultRow = Assert.Single(rows, row => AgwMessageClassifier.IsResult(row.ToChatMessage()!));
        var stored = resultRow.ToChatMessage()!;
        Assert.Equal("result", stored.AdditionalProperties!["type"]?.ToString());
        Assert.Equal("markdown", stored.AdditionalProperties["resultFormat"]?.ToString());
        Assert.Equal(answerRow.Id.ToString("D"), stored.AdditionalProperties["resultSourceMessageId"]?.ToString());
        Assert.Equal("result", resultRow.Metadata!["purpose"].GetString());
        Assert.Equal("message", answerRow.Metadata!["purpose"].GetString());
        Assert.Equal(author, stored.AuthorName);
        Assert.Equal("final answer", stored.Text);
        var replay = await HistoryTestFixture.ReplayAsync(history, agent, session);
        Assert.Equal(["final answer"], replay.Select(message => message.Text));
        Assert.DoesNotContain(replay, message => AgwMessageClassifier.IsResult(message));
    }

    [Fact]
    public void IsResult_TopLevelOrContentMarker_ClassifiesChatAndAgwMessagesAlike()
    {
        var topLevel = new ChatMessage(ChatRole.Assistant, "done")
        {
            AdditionalProperties = new() { ["type"] = "result" },
        };
        var contentLevel = new ChatMessage(
            ChatRole.Assistant,
            [new TextContent("done") { AdditionalProperties = new() { ["type"] = "result" } }]
        );
        var ordinary = new ChatMessage(ChatRole.Assistant, "done")
        {
            AdditionalProperties = new() { ["type"] = "assistant" },
        };
        var restored = JsonSerializer.Deserialize<ChatMessage>(
            JsonSerializer.Serialize(topLevel, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;

        Assert.True(AgwMessageClassifier.IsResult(topLevel));
        Assert.True(AgwMessageClassifier.IsResult(contentLevel));
        Assert.True(AgwMessageClassifier.IsResult(restored));
        Assert.False(AgwMessageClassifier.IsResult(ordinary));
        Assert.True(AgwMessageClassifier.IsResult(topLevel.ToAiMessage()!));
        Assert.True(AgwMessageClassifier.IsResult(contentLevel.ToAiMessage()!));
        Assert.False(AgwMessageClassifier.IsResult(ordinary.ToAiMessage()!));
    }
}
