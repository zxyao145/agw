using System.Security.Claims;
using Agw.Agents.Execution.Agents.History;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using HistoryDatabase = Agw.Agents.Tests.NormalizedHistoryRegressionTests.HistoryDatabase;

namespace Agw.Agents.Tests;

public sealed class NormalizedConversationHistoryTests
{
    [Fact]
    public async Task ProcessAsync_InterleavedProducers_PersistsFirstVisibleOrder()
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var scope = fixture.BeginPersistenceScope();
        var first = fixture.CreateCapture(new CodexMessageAdapter());
        var second = fixture.CreateCapture(new CodexMessageAdapter());
        var token = TestContext.Current.CancellationToken;

        await first.ProcessAsync(new AgentResponseUpdate(ChatRole.Assistant, "a1") { MessageId = "a1" }, token);
        await second.ProcessAsync(new AgentResponseUpdate(ChatRole.Assistant, "b1") { MessageId = "b1" }, token);
        await first.ProcessAsync(new AgentResponseUpdate(ChatRole.Assistant, "a2") { MessageId = "a2" }, token);
        await first.FinishAsync(token);
        await second.FinishAsync(token);
        await ConversationHistoryPersistenceContext.FlushAsync(token);

        Assert.Equal(["a1", "b1", "a2"], (await fixture.ReadAsync()).Select(message => message.Text));
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate)]
    [InlineData(ConversationHistoryWriteMode.Interval)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd)]
    public async Task CompleteAsync_StreamedResponse_PersistsOnceAndEmitsOnlyDeltas(ConversationHistoryWriteMode mode)
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync(mode);
        await using var scope = fixture.BeginPersistenceScope();
        var capture = fixture.CreateCapture(new ClaudeMessageAdapter());
        var token = TestContext.Current.CancellationToken;
        string? id = null;
        var live = new List<AgentResponseUpdate>();
        foreach (var part in new[] { "The", " ", "user", " wants", " a review" })
        {
            await capture.ProcessAsync(
                new AgentResponseUpdate(ChatRole.Assistant, part) { MessageId = "answer" },
                token
            );
            live.AddRange(capture.Drain());
            await ConversationHistoryPersistenceContext.FlushAsync(token);
            var row = Assert.Single(await fixture.ReadAsync());
            id ??= row.MessageId;
            Assert.Equal(id, row.MessageId);
        }

        await capture.CompleteAsync(
            [new ChatMessage(ChatRole.Assistant, "The user wants a review") { MessageId = "answer" }],
            token
        );
        await capture.FinishAsync(token);
        await ConversationHistoryPersistenceContext.FlushAsync(token);

        Assert.Empty(capture.Drain());
        Assert.Equal(["The", " ", "user", " wants", " a review"], live.Select(update => update.Text));
        var stored = Assert.Single(await fixture.ReadAsync());
        Assert.Equal(id, stored.MessageId);
        Assert.Equal(live.ToAgentResponse().Text, stored.Text);
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(stored));
    }

    [Fact]
    public async Task CompleteAsync_CallbackBeforeToolSupplement_PreservesOneMessage()
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync();
        var capture = fixture.CreateCapture(new ClaudeMessageAdapter());
        var token = TestContext.Current.CancellationToken;
        await capture.ProcessAsync(
            new AgentResponseUpdate(ChatRole.Assistant, "Checking ") { MessageId = "answer" },
            token
        );
        var first = Assert.Single(capture.Drain());

        await capture.CompleteAsync(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    [new TextContent("Checking "), new FunctionCallContent("call", "lookup")]
                )
                {
                    MessageId = "answer",
                },
            ],
            token
        );
        Assert.Empty(capture.Drain());
        await capture.ProcessAsync(
            new AgentResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call", "lookup")])
            {
                MessageId = "answer",
            },
            token
        );
        await capture.FinishAsync(token);

        var tool = Assert.Single(capture.Drain());
        Assert.Equal(first.MessageId, tool.MessageId);
        Assert.IsType<FunctionCallContent>(Assert.Single(tool.Contents));
        var stored = Assert.Single(await fixture.ReadAsync());
        Assert.Equal("Checking ", stored.Text);
        Assert.Single(stored.Contents.OfType<FunctionCallContent>());
    }

    [Fact]
    public async Task FinishAsync_LaterDelta_PreservesMessageIdentityAndModelHistory()
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync();
        var capture = fixture.CreateCapture(new ModelMessageAdapter());
        var token = TestContext.Current.CancellationToken;
        await capture.ProcessAsync(
            new AgentResponseUpdate(ChatRole.Assistant, "partial") { MessageId = "answer" },
            token
        );
        var first = Assert.Single(capture.Drain());
        await capture.FinishAsync(token);
        Assert.Empty(capture.Drain());

        await capture.ProcessAsync(
            new AgentResponseUpdate(ChatRole.Assistant, " tail") { MessageId = "answer" },
            token
        );

        Assert.Equal(first.MessageId, Assert.Single(capture.Drain()).MessageId);
        var stored = Assert.Single(await fixture.ReadAsync());
        Assert.Equal("partial tail", stored.Text);
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(stored));
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate)]
    [InlineData(ConversationHistoryWriteMode.Interval)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd)]
    public async Task ProcessAsync_ReusedSourceId_StaysIsolatedAcrossProducers(ConversationHistoryWriteMode mode)
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync(mode);
        var token = TestContext.Current.CancellationToken;
        for (var turn = 0; turn < 2; turn++)
        {
            await using var scope = fixture.BeginPersistenceScope();
            var capture = fixture.CreateCapture(new CodexMessageAdapter());
            foreach (var text in new[] { "a", "a", " ", "b" })
            {
                await capture.ProcessAsync(
                    new AgentResponseUpdate(ChatRole.Assistant, text) { MessageId = "item_0" },
                    token
                );
                await ConversationHistoryPersistenceContext.FlushAsync(token);
            }
            await capture.FinishAsync(token);
            await ConversationHistoryPersistenceContext.FlushAsync(token);
        }

        var rows = await fixture.ReadAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, message => Assert.Equal("aa b", message.Text));
        Assert.NotEqual(rows[0].MessageId, rows[1].MessageId);
    }

    private static IDisposable EnterOwner() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );
}
