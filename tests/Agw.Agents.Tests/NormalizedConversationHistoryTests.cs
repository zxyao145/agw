using Agw.Agents.Execution.Agents.History;
using Agw.Projects.Application.History;
using Agw.Projects.Contracts.History;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed partial class AgentRequestContextAgentTests
{
    [Fact]
    public async Task NormalizedHistory_InterleavedProducers_PersistsFirstVisibleOrder()
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var provider = new NormalizedChatHistoryProvider(fixture.Provider, fixture.Clock);
        var agent = new HistoryNotifyingAgent(provider);
        var firstSession = await InitializeHistorySessionAsync(agent, fixture);
        var secondSession = await InitializeHistorySessionAsync(agent, fixture);
        var token = TestContext.Current.CancellationToken;
        await using var scope = ConversationHistoryPersistenceContext.BeginScope(
            fixture.Provider,
            fixture.ProjectId,
            "streaming",
            0,
            token
        );
        var first = (await provider.BeginAsync(agent, firstSession, [], new CodexMessageAdapter(), true, token))!;
        var second = (await provider.BeginAsync(agent, secondSession, [], new CodexMessageAdapter(), true, token))!;

        await first.ProcessAsync(new AgentResponseUpdate(ChatRole.Assistant, "a1") { MessageId = "a1" }, token);
        await second.ProcessAsync(new AgentResponseUpdate(ChatRole.Assistant, "b1") { MessageId = "b1" }, token);
        await first.ProcessAsync(new AgentResponseUpdate(ChatRole.Assistant, "a2") { MessageId = "a2" }, token);
        await ConversationHistoryPersistenceContext.FlushAsync(token);

        Assert.Equal(["a1", "b1", "a2"], (await fixture.ReadAsync()).Select(row => row.GetText()));
        await first.FinishAsync(ConversationMessageState.Completed, token);
        await second.FinishAsync(ConversationMessageState.Completed, token);
        provider.End(firstSession);
        provider.End(secondSession);
    }

    [Fact]
    public async Task NormalizedHistory_LaterInterruption_PreservesCompletedMessageState()
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var provider = new NormalizedChatHistoryProvider(fixture.Provider, fixture.Clock);
        var agent = new HistoryNotifyingAgent(provider);
        var session = await InitializeHistorySessionAsync(agent, fixture);
        var token = TestContext.Current.CancellationToken;
        var capture = await provider.BeginAsync(agent, session, [], new PiMessageAdapter(), true, token);
        Assert.NotNull(capture);
        var first = new ChatMessage(ChatRole.Assistant, "completed") { MessageId = "first", AuthorName = "pi" };
        await capture.CompleteAsync([first], token);
        // Pi's authoritative callback precedes the matching streamed snapshot.
        await capture.ProcessAsync(
            new AgentResponseUpdate(ChatRole.Assistant, "completed")
            {
                MessageId = "first",
                AuthorName = "pi",
                AdditionalProperties = new() { ["messageSnapshot"] = true },
            },
            token
        );
        await capture.ProcessAsync(
            new AgentResponseUpdate(ChatRole.Assistant, "partial") { MessageId = "second", AuthorName = "pi" },
            token
        );

        await capture.FinishAsync(ConversationMessageState.Interrupted, token);
        provider.End(session);

        var rows = await fixture.ReadAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("completed", rows[0].ToChatMessage()!.AdditionalProperties!["messageState"]?.ToString());
        Assert.Equal("interrupted", rows[1].ToChatMessage()!.AdditionalProperties!["messageState"]?.ToString());
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(rows[0].ToChatMessage()!));
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(rows[1].ToChatMessage()!));
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate)]
    [InlineData(ConversationHistoryWriteMode.Interval)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd)]
    public async Task NormalizedHistory_CrossFlushDeltasAndFinalCallback_UpdatesOneStableRow(
        ConversationHistoryWriteMode mode
    )
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync(mode);
        var provider = new NormalizedChatHistoryProvider(fixture.Provider, fixture.Clock);
        var agent = new HistoryNotifyingAgent(provider);
        var session = await InitializeHistorySessionAsync(agent, fixture);
        var token = TestContext.Current.CancellationToken;
        await using var scope = ConversationHistoryPersistenceContext.BeginScope(
            fixture.Provider,
            fixture.ProjectId,
            "streaming",
            0,
            token
        );
        var capture = await provider.BeginAsync(
            agent,
            session,
            [new ChatMessage(ChatRole.User, "question")],
            new ClaudeMessageAdapter(),
            true,
            token
        );
        Assert.NotNull(capture);
        Guid? recordId = null;
        foreach (var part in new[] { "The", " ", "user", " wants", " a review" })
        {
            await capture.ProcessAsync(
                new AgentResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(part)])
                {
                    MessageId = "answer",
                    AuthorName = "claude-code",
                    AdditionalProperties = new() { ["type"] = "assistant" },
                },
                token
            );
            await capture.ProcessAsync(
                new AgentResponseUpdate(ChatRole.System, "progress")
                {
                    AdditionalProperties = new() { ["type"] = "system", ["subtype"] = "thinking_tokens" },
                },
                token
            );
            await ConversationHistoryPersistenceContext.FlushAsync(token);
            var row = Assert.Single(
                await fixture.ReadAsync(),
                row => row.Metadata?.ContainsKey("producerScopeId") == true
            );
            recordId ??= row.Id;
            Assert.Equal(recordId, row.Id);
        }
        await provider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(
                agent,
                session,
                [],
                [
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new TextReasoningContent("The user wants a review"),
                            new FunctionCallContent("call", "Skill", new Dictionary<string, object?>()),
                        ]
                    )
                    {
                        MessageId = "answer",
                        AuthorName = "claude-code",
                    },
                ]
            ),
            token
        );
        await capture.FinishAsync(ConversationMessageState.Completed, token);
        await ConversationHistoryPersistenceContext.FlushAsync(token);

        var rows = await fixture.ReadAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(recordId, rows[1].Id);
        Assert.Equal(rows[1].Id.ToString("D"), rows[1].ToChatMessage()!.MessageId);
        Assert.Equal("completed", rows[1].ToChatMessage()!.AdditionalProperties!["messageState"]?.ToString());
        Assert.Collection(
            rows[1].ToChatMessage()!.Contents,
            content => Assert.Equal("The user wants a review", Assert.IsType<TextReasoningContent>(content).Text),
            content => Assert.Equal("call", Assert.IsType<FunctionCallContent>(content).CallId)
        );
        provider.End(session);
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate)]
    [InlineData(ConversationHistoryWriteMode.Interval)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd)]
    public async Task NormalizedHistory_CodexDeltasAndReusedSourceIds_StayIsolatedAcrossProducers(
        ConversationHistoryWriteMode mode
    )
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync(mode);
        var provider = new NormalizedChatHistoryProvider(fixture.Provider, fixture.Clock);
        var agent = new HistoryNotifyingAgent(provider);
        var session = await InitializeHistorySessionAsync(agent, fixture);
        var token = TestContext.Current.CancellationToken;
        for (var turn = 0; turn < 2; turn++)
        {
            await using var scope = ConversationHistoryPersistenceContext.BeginScope(
                fixture.Provider,
                fixture.ProjectId,
                "streaming",
                0,
                token
            );
            var capture = await provider.BeginAsync(agent, session, [], new CodexMessageAdapter(), true, token);
            Assert.NotNull(capture);
            foreach (var text in new[] { "a", "a", " ", "b" })
            {
                await capture.ProcessAsync(
                    new AgentResponseUpdate(ChatRole.Assistant, text)
                    {
                        MessageId = "item_0",
                        AuthorName = "codex",
                        AdditionalProperties = new() { ["type"] = "item.updated" },
                    },
                    token
                );
                await ConversationHistoryPersistenceContext.FlushAsync(token);
            }
            await capture.FinishAsync(ConversationMessageState.Completed, token);
            await ConversationHistoryPersistenceContext.FlushAsync(token);
            provider.End(session);
        }

        var rows = await fixture.ReadAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("aa b", row.GetText()));
        Assert.NotEqual(rows[0].Id, rows[1].Id);
    }
}
