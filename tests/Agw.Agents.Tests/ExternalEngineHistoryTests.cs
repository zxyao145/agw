using Agw.Projects.Application.History;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

/// <summary>
/// External Engine 的 SDK 回调经各自适配器进入同一套历史：片段合并、Result、Schema 格式与并行会话。
/// SDK callbacks of External Engines reach one history through their adapters: fragment merging, Results, schema format and parallel sessions.
/// </summary>
public sealed class ExternalEngineHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaudeHistory_InterleavedProgress_PersistsCompleteMessageWithToolOrder(bool includeProgress)
    {
        // Arrange
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.ClaudeCode);
        var agent = new HistoryNotifyingAgent(history);
        var session = await fixture.CreateSessionAsync(agent);
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
        await HistoryTestFixture.RunSdkCallbacksAsync(
            history,
            agent,
            session,
            [new ChatMessage(ChatRole.User, "Review")],
            sdkMessages.ToList()
        );
        var records = await fixture.ReadAsync();

        // Assert
        Assert.Equal(3, records.Count);
        var assistant = records[1].ToChatMessage()!;
        Assert.Equal(records[1].Id.ToString("D"), assistant.MessageId);
        Assert.Equal("assistant-1", assistant.AdditionalProperties!["sourceMessageId"]?.ToString());
        Assert.Equal("claude-code", assistant.AuthorName);
        Assert.Equal("deepseek-v4-pro", assistant.AdditionalProperties["modelName"]?.ToString());
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
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.Pi);
        var agent = new HistoryNotifyingAgent(history);
        var session = await fixture.CreateSessionAsync(agent);
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
        await HistoryTestFixture.RunSdkCallbacksAsync(
            history,
            agent,
            session,
            [new ChatMessage(ChatRole.User, "1+1=?")],
            [answer],
            [result]
        );
        var records = await fixture.ReadAsync();
        var modelHistory = await HistoryTestFixture.ReplayAsync(history, agent, session);

        // Assert
        Assert.Equal(3, records.Count);
        var answerRow = Assert.Single(
            records,
            record => record.ToChatMessage()?.AdditionalProperties?["sourceMessageId"]?.ToString() == "pi-answer"
        );
        var persisted = Assert
            .Single(
                records,
                record => record.ToChatMessage()?.AdditionalProperties?["sourceMessageId"]?.ToString() == "pi-result"
            )
            .ToChatMessage()!;
        Assert.Equal("2", persisted.Text);
        Assert.Equal("result", persisted.AdditionalProperties!["type"]?.ToString());
        Assert.Equal("deepseek-flash", persisted.AdditionalProperties["modelName"]?.ToString());
        Assert.Equal(answerRow.Id.ToString("D"), persisted.AdditionalProperties["resultSourceMessageId"]?.ToString());
        Assert.Equal("pi", persisted.AuthorName);
        Assert.Equal(["1+1=?", "2"], modelHistory.Select(message => message.Text));
    }

    [Fact]
    public async Task PiHistory_ToolResponse_PersistsDisplayOnlyWithoutTransportData()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.Pi);
        var agent = new HistoryNotifyingAgent(history);
        var session = await fixture.CreateSessionAsync(agent);
        var response = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", "done") { RawRepresentation = new object() }]
        );

        await HistoryTestFixture.RunSdkCallbacksAsync(
            history,
            agent,
            session,
            [new ChatMessage(ChatRole.User, "run")],
            [response]
        );

        var persisted = Assert.Single(await fixture.ReadMessagesAsync(), message => message.Role == ChatRole.Tool);
        Assert.Equal("call-1", Assert.IsType<FunctionResultContent>(Assert.Single(persisted.Contents)).CallId);
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(persisted));
        Assert.Equal(["run"], (await HistoryTestFixture.ReplayAsync(history, agent, session)).Select(m => m.Text));
    }

    [Theory]
    [InlineData(EngineKind.Maf)]
    [InlineData(EngineKind.ClaudeCode)]
    [InlineData(EngineKind.Codex)]
    [InlineData(EngineKind.Pi)]
    public async Task SchemaHistory_Result_ReloadsPersistedFormatAcrossEngineAdapters(EngineKind engine)
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(engine, structuredResult: true);
        Assert.Equal([fixture.SessionState.StateKey], history.StateKeys);
        var agent = new HistoryNotifyingAgent(history);
        var session = await fixture.CreateSessionAsync(agent);
        var result = new ChatMessage(ChatRole.Assistant, "{\"approved\":false}")
        {
            MessageId = "schema-result",
            AuthorName = engine.ToString(),
            AdditionalProperties = new() { ["type"] = "result" },
        };

        await HistoryTestFixture.RunSdkCallbacksAsync(
            history,
            agent,
            session,
            [new ChatMessage(ChatRole.User, "Review")],
            [result]
        );

        var records = await fixture.ReadAsync();
        var persisted = Assert
            .Single(
                records,
                record =>
                    record.ToChatMessage()?.AdditionalProperties?["sourceMessageId"]?.ToString() == "schema-result"
            )
            .ToChatMessage()!;
        Assert.Equal("json", persisted.AdditionalProperties!["resultFormat"]?.ToString());
        Assert.Equal(engine.ToString(), persisted.AuthorName);
        Assert.Equal("{\"approved\":false}", persisted.Text);
        Assert.Equal(2, records.Count);
    }

    [Theory]
    [InlineData(EngineKind.ClaudeCode)]
    [InlineData(EngineKind.Pi)]
    public async Task Run_SdkNotifiesHistory_PersistsOriginalInputAndResponseOnce(EngineKind engine)
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(engine);
        var sdk = new HistoryNotifyingAgent(history);
        var agent = HistoryTestFixture.Record(sdk, history, "private memory");
        var session = await fixture.CreateSessionAsync(agent);

        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "original")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Contains("private memory", Assert.Single(sdk.RequestMessages).Text, StringComparison.Ordinal);
        var records = await fixture.ReadAsync();
        Assert.Equal(["original", "answer"], records.Select(record => record.GetText()));
        Assert.Equal(records[0].TaskId, records[1].TaskId);
    }

    [Fact]
    public async Task SharedProvider_ParallelSessions_KeepsRequestsResponsesAndNodeScopesSeparate()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var history = fixture.CreateProvider(EngineKind.Pi);
        var agent = new HistoryNotifyingAgent(historyProvider: null);
        var first = await fixture.CreateSessionAsync(agent);
        var second = await fixture.CreateSessionAsync(agent);
        fixture.SessionState.InitializeSessionState(
            first,
            HistoryTestFixture.ContextId,
            fixture.ProjectId,
            "first",
            "First node"
        );
        fixture.SessionState.InitializeSessionState(
            second,
            HistoryTestFixture.ContextId,
            fixture.ProjectId,
            "second",
            "Second node"
        );
        var token = TestContext.Current.CancellationToken;
        await using (fixture.BeginTurn())
        {
            await Task.WhenAll(
                HistoryTestFixture.RunSdkCallbacksAsync(
                    history,
                    agent,
                    first,
                    [new ChatMessage(ChatRole.User, "first request")],
                    [new ChatMessage(ChatRole.Assistant, "first answer")]
                ),
                HistoryTestFixture.RunSdkCallbacksAsync(
                    history,
                    agent,
                    second,
                    [new ChatMessage(ChatRole.User, "second request")],
                    [new ChatMessage(ChatRole.Assistant, "second answer")]
                )
            );
            Assert.Empty(await fixture.ReadAsync());
            var firstHistory = await HistoryTestFixture.ReplayAsync(history, agent, first);
            var secondHistory = await HistoryTestFixture.ReplayAsync(history, agent, second);
            Assert.Equal(["first request", "first answer"], firstHistory.Select(message => message.Text));
            Assert.Equal(["second request", "second answer"], secondHistory.Select(message => message.Text));
        }
        var records = await fixture.ReadAsync();
        Assert.Equal(4, records.Count);
        Assert.Equal(2, records.GroupBy(record => record.TaskId).Count());
        Assert.Equal(
            ["First node", "Second node"],
            records
                .Select(record => record.ToChatMessage()!)
                .Where(message => message.Role == ChatRole.Assistant)
                .Select(message => message.AdditionalProperties!["nodeName"]?.ToString())
                .Order()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_HistoryWriteFails_ReportsFailureWithoutDuplicatingInput(bool executionFails)
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.Pi);
        var failure = new InvalidOperationException("SDK failed");
        var agent = HistoryTestFixture.Record(
            new HistoryNotifyingAgent(null, executionFails ? failure : null),
            history
        );
        var session = await fixture.CreateSessionAsync(agent);
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

        // 执行失败时收尾写入同样失败并被记录；执行成功时收尾重新提交一次尚未确认的输入。
        // When the execution fails the closing write also fails and is logged; when it succeeds the close resubmits the unacknowledged input once.
        Assert.Equal(
            executionFails ? [] : ["original"],
            (await fixture.ReadAsync()).Select(record => record.GetText())
        );
    }
}
