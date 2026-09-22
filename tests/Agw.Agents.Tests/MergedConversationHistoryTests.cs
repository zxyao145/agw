using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using Agw.Agents.Execution.Agents.ExternalAgents.Pi;
using Agw.Agents.Execution.Agents.History;
using Agw.Projects.Application.History;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed partial class AgentRequestContextAgentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaudeHistory_InterleavedProgress_PersistsCompleteMessageWithToolOrder(bool includeProgress)
    {
        // Arrange
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var provider = new ClaudeCodeChatHistoryProvider(fixture.Provider);
        var agent = new HistoryNotifyingAgent(provider);
        var session = await InitializeHistorySessionAsync(agent, fixture);
        fixture.Provider.StageRequest(session, [new ChatMessage(ChatRole.User, "Review")]);
        var createdAt = new DateTimeOffset(2026, 9, 21, 0, 48, 54, TimeSpan.Zero);
        var updates = new List<AgentResponseUpdate>();
        foreach (var text in new[] { "The", " ", "user", " wants", " a review" })
        {
            updates.Add(
                new AgentResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(text)])
                {
                    MessageId = "assistant-1",
                    AuthorName = "claude-code",
                    CreatedAt = createdAt,
                    AdditionalProperties = new() { ["type"] = "assistant", ["modelName"] = "deepseek-v4-pro" },
                }
            );
            if (includeProgress)
                updates.Add(
                    new AgentResponseUpdate(ChatRole.System, "progress")
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        AdditionalProperties = new() { ["type"] = "system", ["subtype"] = "thinking_tokens" },
                    }
                );
        }
        updates.Add(
            new AgentResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call-1", "Skill", new Dictionary<string, object?> { ["skill"] = "review" })]
            )
            {
                MessageId = "assistant-1",
                AuthorName = "claude-code",
                AdditionalProperties = new() { ["type"] = "assistant", ["modelName"] = "deepseek-v4-pro" },
            }
        );
        var sdkMessages = updates.ToAgentResponse().Messages;
        sdkMessages.Add(new ChatMessage(ChatRole.User, [new FunctionResultContent("call-1", "instructions")]));

        // Act
        await provider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(agent, session, [], sdkMessages),
            TestContext.Current.CancellationToken
        );
        var records = await fixture.ReadAsync();

        // Assert
        Assert.Equal(3, records.Count);
        var assistant = records[1].ToChatMessage()!;
        Assert.Equal("assistant-1", assistant.MessageId);
        Assert.Equal("claude-code", assistant.AuthorName);
        Assert.Equal("deepseek-v4-pro", assistant.AdditionalProperties!["modelName"]?.ToString());
        Assert.Equal(createdAt, assistant.CreatedAt);
        Assert.Collection(
            assistant.Contents,
            content => Assert.Equal("The user wants a review", Assert.IsType<TextReasoningContent>(content).Text),
            content =>
            {
                var call = Assert.IsType<FunctionCallContent>(content);
                Assert.Equal("call-1", call.CallId);
                Assert.Equal("Skill", call.Name);
                Assert.Equal("review", call.Arguments!["skill"]?.ToString());
            }
        );
        Assert.Equal(
            "call-1",
            Assert.IsType<FunctionResultContent>(Assert.Single(records[2].ToChatMessage()!.Contents)).CallId
        );
        Assert.Equal("The", Assert.IsType<TextReasoningContent>(updates[0].Contents[0]).Text);
    }

    [Fact]
    public async Task PiResult_Reload_PreservesResultWithoutDuplicatingModelHistory()
    {
        // Arrange
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var provider = new PiChatHistoryProvider(fixture.Provider);
        var agent = new HistoryNotifyingAgent(provider);
        var session = await InitializeHistorySessionAsync(agent, fixture);
        fixture.Provider.StageRequest(session, [new ChatMessage(ChatRole.User, "1+1=?")]);
        var answer = new ChatMessage(ChatRole.Assistant, "2") { AuthorName = "pi", MessageId = "pi-answer" };
        var result = new ChatMessage(ChatRole.Assistant, "2")
        {
            AuthorName = "pi",
            MessageId = "pi-result",
            AdditionalProperties = new()
            {
                ["type"] = "result",
                ["modelName"] = "deepseek-flash",
                ["resultSourceMessageId"] = "pi-answer",
            },
        };

        // Act
        foreach (var message in new[] { answer, result })
        {
            await provider.InvokedAsync(
                new ChatHistoryProvider.InvokedContext(agent, session, [], [message]),
                TestContext.Current.CancellationToken
            );
        }
        var records = await fixture.ReadAsync();
        var modelHistory = await provider.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(agent, session, []),
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(3, records.Count);
        var persisted = Assert
            .Single(records, record => record.ToChatMessage()?.MessageId == "pi-result")
            .ToChatMessage()!;
        Assert.Equal("2", persisted.Text);
        Assert.Equal("result", persisted.AdditionalProperties!["type"]?.ToString());
        Assert.Equal("deepseek-flash", persisted.AdditionalProperties["modelName"]?.ToString());
        Assert.Equal("pi-answer", persisted.AdditionalProperties["resultSourceMessageId"]?.ToString());
        Assert.Equal("pi", persisted.AuthorName);
        Assert.Equal(["1+1=?", "2"], modelHistory.Select(message => message.Text));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("claude-code")]
    [InlineData("codex")]
    [InlineData("pi")]
    public async Task SchemaHistory_Result_ReloadsPersistedFormatAcrossSdkAdapters(string kind)
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        ChatHistoryProvider provider = new ResponseSchemaChatHistoryProvider(fixture.Provider);
        provider = kind switch
        {
            "claude-code" => new ClaudeCodeChatHistoryProvider(provider),
            "pi" => new PiChatHistoryProvider(provider),
            _ => provider,
        };
        Assert.Same(fixture.Provider, provider.GetService<IConversationHistoryRequests>());
        Assert.Equal(fixture.Provider.StateKeys, provider.StateKeys);
        var agent = new HistoryNotifyingAgent(provider);
        var session = await InitializeHistorySessionAsync(agent, fixture);
        fixture.Provider.StageRequest(session, [new ChatMessage(ChatRole.User, "Review")]);
        var result = new ChatMessage(ChatRole.Assistant, "{\"approved\":false}")
        {
            MessageId = "schema-result",
            AuthorName = kind,
            AdditionalProperties = new() { ["type"] = "result" },
        };

        await provider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(agent, session, [], [result]),
            TestContext.Current.CancellationToken
        );

        var records = await fixture.ReadAsync();
        var persisted = Assert
            .Single(records, record => record.ToChatMessage()?.MessageId == "schema-result")
            .ToChatMessage()!;
        Assert.Equal("json", persisted.AdditionalProperties!["resultFormat"]?.ToString());
        Assert.Equal(kind, persisted.AuthorName);
        Assert.Equal("{\"approved\":false}", persisted.Text);
        Assert.Equal(2, records.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkAdapter_DirectEfProvider_PersistsOriginalInputAndResponseOnce(bool claude)
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        ChatHistoryProvider adapter = claude
            ? new ClaudeCodeChatHistoryProvider(fixture.Provider)
            : new PiChatHistoryProvider(fixture.Provider);
        Assert.Same(fixture.Provider, adapter.GetService<IConversationHistoryRequests>());
        var sdk = new HistoryNotifyingAgent(adapter);
        var agent = CreateAgent(sdk, adapter, "private memory");
        var session = await InitializeHistorySessionAsync(agent, fixture);

        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "original")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Contains("private memory", Assert.Single(sdk.RequestMessages).Text, StringComparison.Ordinal);
        var records = await fixture.ReadAsync();
        Assert.Equal(["original", "answer"], records.Select(record => record.GetText()));
        Assert.Equal(records[0].TaskId, records[1].TaskId);
        await fixture.Provider.PersistPendingAsync(agent, session, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await fixture.ReadAsync()).Count);
    }

    [Fact]
    public async Task SharedProvider_ParallelSessions_KeepsRequestsResponsesAndNodeScopesSeparate()
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var agent = new HistoryNotifyingAgent(historyProvider: null);
        var first = await InitializeHistorySessionAsync(agent, fixture);
        var second = await InitializeHistorySessionAsync(agent, fixture);
        fixture.Provider.InitializeSessionState(first, "streaming", fixture.ProjectId, "first", "First node");
        fixture.Provider.InitializeSessionState(second, "streaming", fixture.ProjectId, "second", "Second node");
        await using (
            ConversationHistoryPersistenceContext.BeginScope(
                fixture.Provider,
                fixture.ProjectId,
                "streaming",
                0,
                TestContext.Current.CancellationToken
            )
        )
        {
            fixture.Provider.StageRequest(first, [new ChatMessage(ChatRole.User, "first request")]);
            fixture.Provider.StageRequest(second, [new ChatMessage(ChatRole.User, "second request")]);
            await Task.WhenAll(
                fixture
                    .Provider.InvokedAsync(
                        new ChatHistoryProvider.InvokedContext(
                            agent,
                            first,
                            [],
                            [new ChatMessage(ChatRole.Assistant, "first answer")]
                        ),
                        TestContext.Current.CancellationToken
                    )
                    .AsTask(),
                fixture
                    .Provider.InvokedAsync(
                        new ChatHistoryProvider.InvokedContext(
                            agent,
                            second,
                            [],
                            [new ChatMessage(ChatRole.Assistant, "second answer")]
                        ),
                        TestContext.Current.CancellationToken
                    )
                    .AsTask()
            );
            await fixture.Provider.PersistPendingAsync(agent, first, TestContext.Current.CancellationToken);
            await fixture.Provider.PersistPendingAsync(agent, second, TestContext.Current.CancellationToken);
            Assert.Empty(await fixture.ReadAsync());
            var firstHistory = await fixture.Provider.InvokingAsync(
                new ChatHistoryProvider.InvokingContext(agent, first, []),
                TestContext.Current.CancellationToken
            );
            var secondHistory = await fixture.Provider.InvokingAsync(
                new ChatHistoryProvider.InvokingContext(agent, second, []),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(["first request", "first answer"], firstHistory.Select(message => message.Text));
            Assert.Equal(["second request", "second answer"], secondHistory.Select(message => message.Text));
        }
        var records = await fixture.ReadAsync();
        Assert.Equal(4, records.Count);
        Assert.Equal(2, records.GroupBy(record => record.TaskId).Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalRequestSave_Fails_ReportsFailureAndReleasesStagedInput(bool executionFails)
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var failure = new InvalidOperationException("SDK failed");
        var sdk = new HistoryNotifyingAgent(null, executionFails ? failure : null);
        var agent = CreateAgent(sdk, fixture.Provider, memoryText: null);
        var session = await InitializeHistorySessionAsync(agent, fixture);
        fixture.FailNextContext = true;
        Task Run() =>
            agent.RunAsync(
                [new ChatMessage(ChatRole.User, "original")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            );

        if (executionFails)
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(Run));
        else
            await Assert.ThrowsAsync<DbUpdateException>(Run);

        await fixture.Provider.PersistPendingAsync(agent, session, TestContext.Current.CancellationToken);
        Assert.Empty(await fixture.ReadAsync());
    }
}
