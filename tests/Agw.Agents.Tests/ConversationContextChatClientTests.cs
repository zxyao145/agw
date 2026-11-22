using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Context.Conversation;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Shared.Exceptions;
using Agw.Tools.Impl.ToolBlocks.Mode;
using Agw.Tools.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable MAAI001

namespace Agw.Agents.Tests;

public sealed class ConversationContextChatClientTests : IDisposable
{
    private const string Header =
        "<system-reminder>\nContext for this conversation.\nUse it when it is relevant to the request.\n\n";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task GetResponseAsync_FirstRequestOfConversation_ComputesAndPrependsConversationContext()
    {
        var model = new RecordingModelClient();
        await using var capabilities = AgwAgentTestHost.Capabilities(
            new ToolContribution
            {
                ConversationContextSources =
                {
                    new CountingSource("User Memory", "Reply in Chinese."),
                    new CountingSource("Project Memory Index", "architecture.md"),
                },
            }
        );
        var agent = CreateAgent(model, capabilities);
        var session = await agent.CreateSessionAsync(Ct);

        await agent.RunAsync([User("u1", "hello")], session, cancellationToken: Ct);

        var messages = Assert.Single(model.Requests).Messages;
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal(
            Header + "# User Memory\nReply in Chinese.\n\n# Project Memory Index\narchitecture.md\n</system-reminder>",
            messages[0].Text
        );
        Assert.Equal("hello", messages[1].Text);
        var savedContext = session.StateBag.Serialize().GetProperty("agw.conversationContext");
        Assert.True(savedContext.GetProperty("computed").GetBoolean());
        Assert.Equal("u1", savedContext.GetProperty("anchorMessageId").GetString());
        Assert.Equal(messages[0].Text, savedContext.GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetResponseAsync_MissingSession_ThrowsBeforeLoadingContext()
    {
        var model = new RecordingModelClient();
        var source = new CountingSource("User Memory", "profile");
        using var client = new ConversationContextChatClient(model, [source], [], null);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            client.GetResponseAsync([User("u1", "hello")], cancellationToken: Ct)
        );

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
        Assert.Equal(0, source.Calls);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task GetResponseAsync_LaterRequest_ReusesStoredConversationContext()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var model = new RecordingModelClient();
        var source = new CountingSource("User Memory", "v1");
        await using var capabilities = AgwAgentTestHost.Capabilities(
            new ToolContribution { ConversationContextSources = { source } }
        );
        var agent = CreateRecordedAgent(fixture, model, fixture.CreateProvider(), capabilities);
        var session = await fixture.CreateSessionAsync(agent);
        var first = User(Guid.CreateVersion7().ToString("D"), "one");
        await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, first);
        var persisted = Assert.Single(await fixture.ReadMessagesAsync(), message => message.Text == "one");
        var serializedSession = await agent.SerializeSessionAsync(session, cancellationToken: Ct);
        source.Content = "v2";
        session = await agent.DeserializeSessionAsync(serializedSession, cancellationToken: Ct);

        await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, User(Guid.CreateVersion7().ToString("D"), "two"));

        var replayed = model.Requests[1].Messages[1];
        Assert.Equal("one", replayed.Text);
        Assert.Equal(first.MessageId, persisted.MessageId);
        Assert.Equal(persisted.MessageId, replayed.MessageId);
        Assert.Equal(persisted.MessageId, ReadAnchorMessageId(session));
        Assert.Equal(model.Requests[0].Messages[0].Text, model.Requests[1].Messages[0].Text);
        Assert.Equal(1, source.Calls);
        Assert.Single(model.Requests[1].Messages, message => message.Text.StartsWith(Header, StringComparison.Ordinal));
    }

    private static string? ReadAnchorMessageId(AgentSession session) =>
        session.StateBag.Serialize().GetProperty("agw.conversationContext").GetProperty("anchorMessageId").GetString();

    [Fact]
    public async Task GetResponseAsync_AllSourcesEmpty_SendsNoContextAndDoesNotRecompute()
    {
        var model = new RecordingModelClient();
        var source = new CountingSource("User Memory", null);
        await using var capabilities = AgwAgentTestHost.Capabilities(
            new ToolContribution { ConversationContextSources = { source } }
        );
        var agent = CreateAgent(model, capabilities);
        var session = await agent.CreateSessionAsync(Ct);

        await agent.RunAsync([User("u1", "one")], session, cancellationToken: Ct);
        source.Content = "added during this conversation";
        await agent.RunAsync([User("u2", "two")], session, cancellationToken: Ct);

        Assert.All(model.Requests, request => Assert.Equal("one", request.Messages[0].Text));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task GetResponseAsync_FirstMessageChangedByTruncation_RecomputesConversationContext()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var model = new RecordingModelClient();
        var source = new CountingSource("User Memory", "v1");
        await using var capabilities = AgwAgentTestHost.Capabilities(
            new ToolContribution { ConversationContextSources = { source } }
        );
        var strategy = ContextWindowCompactionPipeline.Create(100_000, 20_000);
        var agent = CreateRecordedAgent(fixture, model, fixture.CreateProvider(), capabilities, strategy);
        var session = await fixture.CreateSessionAsync(agent);
        var first = User(Guid.CreateVersion7().ToString("D"), "short");
        await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, first);
        source.Content = "v2";
        var longMessages = Enumerable
            .Range(1, 14)
            .Select(index => User(Guid.CreateVersion7().ToString("D"), $"u{index}:" + new string('x', 20_000)))
            .ToArray();

        await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, longMessages);

        Assert.Equal(2, source.Calls);
        var messages = model.Requests[^1].Messages;
        Assert.Contains("v2", messages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(messages, message => message.MessageId == first.MessageId);
        Assert.Equal(messages[1].MessageId, ReadAnchorMessageId(session));
    }

    [Fact]
    public async Task GetResponseAsync_ToolResultsEvicted_KeepsConversationContext()
    {
        var model = new RecordingModelClient(
            (index, _) => index < 14 ? [new FunctionCallContent($"call-{index}", "lookup")] : [new TextContent("done")]
        );
        var source = new CountingSource("User Memory", "v1");
        var lookup = AIFunctionFactory.Create(() => new string('x', 20_000), "lookup");
        await using var capabilities = AgwAgentTestHost.Capabilities(
            new ToolContribution { Tools = { lookup }, ConversationContextSources = { source } }
        );
        var agent = CreateAgent(model, capabilities, ContextWindowCompactionPipeline.Create(1_000_000, 128_000));
        var session = await agent.CreateSessionAsync(Ct);

        await agent.RunAsync([User("u1", "start")], session, cancellationToken: Ct);

        Assert.Equal(1, source.Calls);
        Assert.Contains(
            model.Requests[^1].Messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
            result => Equals(result.Result, ToolResultEvictionCompactionStrategy.EvictedResultText)
        );
        Assert.All(
            model.Requests,
            request => Assert.Equal(model.Requests[0].Messages[0].Text, request.Messages[0].Text)
        );
    }

    [Fact]
    public async Task GetResponseAsync_ContextAttachment_PersistsRowWithTypeAndSameMessageId()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var model = new RecordingModelClient();
        var history = fixture.CreateProvider();
        var mode = new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = "plan" });
        await using var capabilities = AgwAgentTestHost.Capabilities(
            new ToolContribution { ContextAttachmentSources = { new PlanModeAttachmentSource(mode) } }
        );
        var agent = CreateRecordedAgent(fixture, model, history, capabilities);
        var session = await fixture.CreateSessionAsync(agent);

        await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, User(Guid.CreateVersion7().ToString("D"), "one"));
        await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, User(Guid.CreateVersion7().ToString("D"), "two"));

        var sent = model.Requests[0].Messages[^1];
        Assert.StartsWith("<system-reminder>\nPlan mode is active.", sent.Text);
        Assert.EndsWith("\n</system-reminder>", sent.Text);
        var row = Assert.Single(
            await fixture.ReadMessagesAsync(),
            message => ContextAttachmentHistory.GetAttachmentType(message) == ContextAttachmentTypes.PlanMode
        );
        Assert.Equal(sent.MessageId, row.MessageId);
        Assert.Equal(sent.Text, row.Text);
        Assert.Equal("0", row.AdditionalProperties?.GetValueOrDefault("stepIndex")?.ToString());
        Assert.True(
            row.AdditionalProperties?.ContainsKey(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey)
        );
        var first = model.Requests[0].Messages.Select(message => message.Text).ToArray();
        Assert.Equal(first, model.Requests[1].Messages.Take(first.Length).Select(message => message.Text));
        Assert.Single(model.Requests[1].Messages, message => message.Text == sent.Text);
    }

    [Fact]
    public async Task GetResponseAsync_RerunAfterFailedModelCall_DoesNotWriteAttachmentAgain()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider();
        var mode = new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = "plan" });
        var model = new RecordingModelClient(
            (index, _) => index == 0 ? throw new InvalidOperationException("model failed") : [new TextContent("done")]
        );
        await using var capabilities = AgwAgentTestHost.Capabilities(
            new ToolContribution { ContextAttachmentSources = { new PlanModeAttachmentSource(mode) } }
        );
        var agent = CreateRecordedAgent(fixture, model, history, capabilities);
        var session = await fixture.CreateSessionAsync(agent);
        var input = User(Guid.CreateVersion7().ToString("D"), "one");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgwAgentTestHost.RunTurnAsync(fixture, agent, session, input)
        );
        var saved = Assert.Single(
            await fixture.ReadMessagesAsync(),
            message => ContextAttachmentHistory.GetAttachmentType(message) == ContextAttachmentTypes.PlanMode
        );
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(saved));
        await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, input);

        Assert.Equal(
            model.Requests[0].Messages.Select(message => message.MessageId),
            model.Requests[1].Messages.Select(message => message.MessageId)
        );
        var row = Assert.Single(
            await fixture.ReadMessagesAsync(),
            message => ContextAttachmentHistory.GetAttachmentType(message) == ContextAttachmentTypes.PlanMode
        );
        Assert.Single(model.Requests[1].Messages, message => message.MessageId == row.MessageId);
    }

    [Fact]
    public async Task AsAgwAgent_AttachmentSourcesWithoutWriter_ThrowsAgentExecutionFailed()
    {
        using var model = new RecordingModelClient();
        var mode = new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = "plan" });
        await using var capabilities = AgwAgentTestHost.Capabilities(
            new ToolContribution { ContextAttachmentSources = { new PlanModeAttachmentSource(mode) } }
        );
        var exception = Assert.Throws<AgwException>(() => CreateAgent(model, capabilities));
        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
        Assert.Empty(model.Requests);
    }

    private static AIAgent CreateRecordedAgent(
        HistoryTestFixture fixture,
        RecordingModelClient model,
        AgwChatHistoryProvider history,
        AgentCapabilityComposition capabilities,
        CompactionStrategy? strategy = null
    ) =>
        HistoryTestFixture.Record(
            model.AsAgwAgent(
                AgwAgentTestHost.Definition(
                    history,
                    new CompactionProvider(
                        strategy ?? ContextWindowCompactionPipeline.Create(256_000, 64_000),
                        stateKey: "test-compaction"
                    )
                ),
                capabilities,
                NullLoggerFactory.Instance,
                fixture.Services
            ),
            history
        );

    private AIAgent CreateAgent(
        RecordingModelClient model,
        AgentCapabilityComposition capabilities,
        CompactionStrategy? strategy = null
    ) =>
        model.AsAgwAgent(
            AgwAgentTestHost.Definition(
                new InMemoryChatHistoryProvider(),
                strategy == null ? null : new CompactionProvider(strategy, stateKey: "test-compaction")
            ),
            capabilities,
            NullLoggerFactory.Instance,
            _services
        );

    private static ChatMessage User(string id, string text) => new(ChatRole.User, text) { MessageId = id };

    private sealed class CountingSource : IConversationContextSource
    {
        public CountingSource(string title, string? content)
        {
            Title = title;
            Content = content;
        }

        public string Title { get; }
        public string? Content { get; set; }
        public int Calls { get; private set; }

        public ValueTask<string?> GetContentAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(Content);
        }
    }
}
