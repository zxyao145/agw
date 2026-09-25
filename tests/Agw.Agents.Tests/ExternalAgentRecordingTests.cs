using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Agw.Agents.Execution.Agentflows.Context;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using Agw.Agents.Execution.Agents.History;
using Agw.Projects.Application.History;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

/// <summary>
/// External Agent 的流式输出经 HistoryRecordingAgent 写入真实历史：输出顺序、失败与取消、展示消息及 Claude 会话捕获。
/// External Agent streams reach the real history through HistoryRecordingAgent: output order, failures and cancellation, display messages and Claude session capture.
/// </summary>
public sealed class ExternalAgentRecordingTests
{
    [Fact]
    public async Task RunStreamingAsync_ResultDeltas_PersistTheSameTimestampAsLiveOutput()
    {
        // Arrange
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var createdAt = new DateTimeOffset(2026, 9, 20, 13, 38, 0, TimeSpan.Zero);
        var inner = new PausableExternalAgent();
        var agent = HistoryTestFixture.Record(inner, fixture.CreateProvider(EngineKind.Codex));
        var session = await fixture.CreateSessionAsync(agent);
        foreach (var timestamp in new[] { createdAt, createdAt.AddMinutes(1) })
        {
            inner.Emit(
                new AgentResponseUpdate(ChatRole.Assistant, "result text")
                {
                    MessageId = "result",
                    CreatedAt = timestamp,
                    AdditionalProperties = new() { ["type"] = "result" },
                }
            );
        }
        inner.Complete();

        // Act
        var output = (await CollectAsync(agent.RunStreamingAsync("question", session, cancellationToken: Token)))
            .Select(update => update.ToAiMessage()!)
            .ToList();

        // Assert
        Assert.Equal(2, output.Count);
        Assert.All(output, message => Assert.Equal(createdAt, message.CreatedAt));
        var result = Assert.Single(await fixture.ReadMessagesAsync(), message => message.Role == ChatRole.Assistant);
        Assert.Equal(createdAt, result.ToAiMessage()!.CreatedAt);
        Assert.Equal(output[0].MessageId, result.MessageId);
    }

    [Fact]
    public async Task RunStreamingAsync_NoResponse_PersistsOnlyInput()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var inner = new PausableExternalAgent();
        var agent = HistoryTestFixture.Record(inner, fixture.CreateProvider(EngineKind.Codex));
        var session = await fixture.CreateSessionAsync(agent);
        await using var enumerator = agent
            .RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
            .GetAsyncEnumerator(Token);

        var firstUpdate = enumerator.MoveNextAsync().AsTask();
        await inner.Started.WaitAsync(Token);

        Assert.Empty(await fixture.ReadAsync());
        Assert.False(firstUpdate.IsCompleted);

        inner.Complete();
        Assert.False(await firstUpdate);
        Assert.Equal(["request"], (await fixture.ReadMessagesAsync()).Select(message => message.Text));
    }

    [Fact]
    public async Task RunStreamingAsync_ItemsInsideTurn_PersistInArrivalOrderWhenTurnEnds()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var inner = new PausableExternalAgent();
        var agent = HistoryTestFixture.Record(inner, fixture.CreateProvider(EngineKind.Codex));
        var session = await fixture.CreateSessionAsync(agent);
        for (var index = 0; index < 20; index++)
            inner.Emit(new AgentResponseUpdate(ChatRole.Assistant, $"update-{index}") { MessageId = $"item-{index}" });
        inner.Complete();

        await using (fixture.BeginTurn())
        {
            var updates = await CollectAsync(
                agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
            );
            Assert.Equal(20, updates.Count);
            Assert.Empty(await fixture.ReadAsync());
        }

        Assert.Equal(
            ["request", .. Enumerable.Range(0, 20).Select(index => $"update-{index}")],
            (await fixture.ReadMessagesAsync()).Select(message => message.Text)
        );
    }

    [Fact]
    public async Task RunStreamingAsync_WhenExternalAgentPauses_FlushesOnInterval()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var inner = new PausableExternalAgent();
        var agent = HistoryTestFixture.Record(inner, fixture.CreateProvider(EngineKind.Codex));
        var session = await fixture.CreateSessionAsync(agent);
        await using (fixture.BeginTurn())
        {
            await using var enumerator = agent
                .RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
                .GetAsyncEnumerator(Token);

            inner.Emit(new AgentResponseUpdate(ChatRole.Assistant, "update") { MessageId = "item-1" });
            Assert.True(await enumerator.MoveNextAsync());
            var pendingUpdate = enumerator.MoveNextAsync().AsTask();
            Assert.Empty(await fixture.ReadAsync());

            await fixture.AdvanceFlushTimerAsync();
            await fixture.WaitForCommitAsync();

            Assert.Equal(["request", "update"], (await fixture.ReadMessagesAsync()).Select(message => message.Text));
            Assert.False(pendingUpdate.IsCompleted);
            inner.Complete();
            Assert.False(await pendingUpdate);
        }
    }

    [Fact]
    public async Task RunStreamingAsync_OnNormalCompletion_PersistsEachMessageOnce()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var inner = new PausableExternalAgent();
        var agent = HistoryTestFixture.Record(inner, fixture.CreateProvider(EngineKind.Codex));
        var session = await fixture.CreateSessionAsync(agent);
        inner.Emit(new AgentResponseUpdate(ChatRole.Assistant, "first") { MessageId = "item-1" });
        inner.Emit(new AgentResponseUpdate(ChatRole.Assistant, "second") { MessageId = "item-2" });
        inner.Complete();

        var updates = await CollectAsync(
            agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
        );

        Assert.Equal(["first", "second"], updates.Select(update => update.Text));
        var stored = await fixture.ReadMessagesAsync();
        Assert.Equal(["request", "first", "second"], stored.Select(message => message.Text));
        Assert.All(stored, message => Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(message)));
    }

    [Fact]
    public async Task RunStreamingAsync_WhenCancelled_PersistsProducedMessages()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var inner = new PausableExternalAgent();
        var agent = HistoryTestFixture.Record(inner, fixture.CreateProvider(EngineKind.Codex));
        var session = await fixture.CreateSessionAsync(agent);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: cancellation.Token
            )
            .GetAsyncEnumerator(cancellation.Token);
        inner.Emit(new AgentResponseUpdate(ChatRole.Assistant, "before cancellation") { MessageId = "item-1" });
        Assert.True(await enumerator.MoveNextAsync());

        var pendingUpdate = enumerator.MoveNextAsync().AsTask();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingUpdate);
        var stored = await fixture.ReadMessagesAsync();
        Assert.Equal(["request", "before cancellation"], stored.Select(message => message.Text));
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(stored[1]));
    }

    [Fact]
    public async Task RunStreamingAsync_WhenInnerAgentFails_PersistsProducedMessagesAndPreservesFailure()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var inner = new PausableExternalAgent();
        var agent = HistoryTestFixture.Record(inner, fixture.CreateProvider(EngineKind.Codex));
        var session = await fixture.CreateSessionAsync(agent);
        await using var enumerator = agent
            .RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
            .GetAsyncEnumerator(Token);
        inner.Emit(new AgentResponseUpdate(ChatRole.Assistant, "before failure") { MessageId = "item-1" });
        Assert.True(await enumerator.MoveNextAsync());
        inner.Fail(new InvalidOperationException("external failure"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal("external failure", exception.Message);
        Assert.Equal(
            ["request", "before failure"],
            (await fixture.ReadMessagesAsync()).Select(message => message.Text)
        );
    }

    [Fact]
    public async Task RunStreamingAsync_WhenConsumerDisposesEarly_PersistsProducedMessages()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var inner = new PausableExternalAgent();
        var agent = HistoryTestFixture.Record(inner, fixture.CreateProvider(EngineKind.Codex));
        var session = await fixture.CreateSessionAsync(agent);
        var enumerator = agent
            .RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
            .GetAsyncEnumerator(Token);
        inner.Emit(new AgentResponseUpdate(ChatRole.Assistant, "before disposal") { MessageId = "item-1" });
        Assert.True(await enumerator.MoveNextAsync());

        await enumerator.DisposeAsync();

        Assert.Equal(
            ["request", "before disposal"],
            (await fixture.ReadMessagesAsync()).Select(message => message.Text)
        );
        Assert.True(inner.StreamDisposed);
    }

    [Fact]
    public async Task RunStreamingAsync_DisplayOnlyEvents_AreMarkedAndEmptyControlEventsAreSkipped()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var inner = new PausableExternalAgent();
        var agent = HistoryTestFixture.Record(inner, fixture.CreateProvider(EngineKind.Codex));
        var session = await fixture.CreateSessionAsync(agent);
        inner.Emit(
            new AgentResponseUpdate(ChatRole.System, "Todo list")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = "todo" },
            }
        );
        inner.Emit(new AgentResponseUpdate(ChatRole.Assistant, "answer") { MessageId = "item-1" });
        inner.Emit(new AgentResponseUpdate { Role = ChatRole.System });
        inner.Complete();

        var updates = await CollectAsync(
            agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
        );

        Assert.Equal(["Todo list", "answer"], updates.Select(update => update.Text).Where(text => text.Length > 0));
        var stored = await fixture.ReadMessagesAsync();
        Assert.Equal(["request", "Todo list", "answer"], stored.Select(message => message.Text));
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(stored[1]));
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(stored[2]));
    }

    [Fact]
    public async Task RunStreamingAsync_ToolResponse_IsMarkedDisplayOnly()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var inner = new PausableExternalAgent();
        var agent = HistoryTestFixture.Record(inner, fixture.CreateProvider(EngineKind.Codex));
        var session = await fixture.CreateSessionAsync(agent);
        inner.Emit(
            new AgentResponseUpdate(
                ChatRole.Tool,
                [new FunctionResultContent("tool-1", new Dictionary<string, object> { ["detail"] = "boom" })]
            )
        );
        inner.Complete();

        await CollectAsync(
            agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
        );

        var message = Assert.Single(await fixture.ReadMessagesAsync(), message => message.Role == ChatRole.Tool);
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(message));
        Assert.Equal("tool-1", Assert.IsType<FunctionResultContent>(Assert.Single(message.Contents)).CallId);
    }

    [Theory]
    [InlineData("thinking_tokens")]
    [InlineData("status")]
    [InlineData("vcs_state_changed")]
    [InlineData("future_unknown_event")]
    public async Task ClaudeResponse_WithoutSessionCallback_FiltersNotificationsAndPreservesErrors(string subtype)
    {
        // Arrange: reproduce the SDK contract with both JSON content and complete event metadata.
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var notification = CreateClaudeSystemMessage(subtype, Guid.CreateVersion7());
        var retry = new ChatMessage(ChatRole.System, [new ErrorContent("retry")])
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["type"] = "system",
                ["subtype"] = "api_retry",
            },
        };
        var answer = new ChatMessage(ChatRole.Assistant, "answer");
        var inner = new PausableExternalAgent { NonStreamingMessages = [notification, retry, answer] };
        var agent = new ClaudeCodeProviderSessionTrackingAgent(inner, null);
        var session = await agent.CreateSessionAsync(Token);
        inner.Emit(
            new AgentResponseUpdate
            {
                Role = null,
                Contents = notification.Contents,
                AdditionalProperties = notification.AdditionalProperties,
            }
        );
        inner.Emit(
            new AgentResponseUpdate(ChatRole.System, [new ErrorContent("retry")])
            {
                AdditionalProperties = retry.AdditionalProperties,
            }
        );
        inner.Emit(new AgentResponseUpdate(ChatRole.Assistant, "answer"));
        inner.Complete();

        // Act
        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "request")],
            session,
            cancellationToken: Token
        );
        var updates = await CollectAsync(
            agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
        );
        var recording = fixture.CreateRecording(new ClaudeMessageAdapter());
        await recording.CalibrateAsync([notification, retry, answer], null, Token);
        await recording.FinishAsync(completed: true, Token);

        // Assert
        Assert.Equal(2, (await fixture.ReadAsync()).Count);
        Assert.Equal(2, response.Messages.Count);
        Assert.Equal(2, updates.Count);
        Assert.IsType<ErrorContent>(Assert.Single(response.Messages[0].Contents));
        Assert.IsType<ErrorContent>(Assert.Single(updates[0].Contents));
        Assert.Equal("answer", response.Messages[1].Text);
        Assert.Equal("answer", updates[1].Text);
        Assert.Single(notification.Contents);
    }

    [Fact]
    public async Task RunStreamingAsync_ClaudeInit_CapturesProviderSessionOnceWithoutDisplayingNotifications()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var inner = new PausableExternalAgent();
        var capturedSessionIds = new List<string>();
        var agent = CreateClaudeAgent(
            inner,
            fixture,
            (providerSessionId, _) =>
            {
                capturedSessionIds.Add(providerSessionId);
                return ValueTask.CompletedTask;
            }
        );
        var session = await fixture.CreateSessionAsync(agent);
        var expectedSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        inner.Emit(CreateClaudeInitUpdate(expectedSessionId));
        inner.Emit(CreateClaudeInitUpdate(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        inner.Complete();

        var updates = await CollectAsync(
            agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
        );

        Assert.Empty(updates);
        Assert.Equal(expectedSessionId.Normalize(), Assert.Single(capturedSessionIds));
        Assert.Equal(["request"], (await fixture.ReadMessagesAsync()).Select(message => message.Text));
    }

    [Fact]
    public async Task RunAsync_ClaudeInit_CapturesProviderSession()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var expectedSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var inner = new PausableExternalAgent
        {
            NonStreamingMessages =
            [
                CreateClaudeInitMessage(expectedSessionId),
                new ChatMessage(ChatRole.Assistant, "non-streaming answer"),
            ],
        };
        string? capturedSessionId = null;
        var agent = CreateClaudeAgent(
            inner,
            fixture,
            (providerSessionId, _) =>
            {
                capturedSessionId = providerSessionId;
                return ValueTask.CompletedTask;
            }
        );
        var session = await fixture.CreateSessionAsync(agent);

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "request")],
            session,
            cancellationToken: Token
        );

        Assert.Equal(expectedSessionId.Normalize(), capturedSessionId);
        Assert.Single(response.Messages);
        Assert.Equal(
            ["request", "non-streaming answer"],
            (await fixture.ReadMessagesAsync()).Select(message => message.Text)
        );
    }

    [Fact]
    public async Task RunStreamingAsync_InvalidOrLegacyClaudeInit_DoesNotCaptureOrChangeUpdates()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var inner = new PausableExternalAgent();
        var callbackCount = 0;
        var agent = CreateClaudeAgent(
            inner,
            fixture,
            (_, _) =>
            {
                callbackCount++;
                return ValueTask.CompletedTask;
            }
        );
        var session = await fixture.CreateSessionAsync(agent);
        inner.Emit(CreateClaudeInitUpdate("{bad json"));
        inner.Emit(CreateClaudeInitUpdate(JsonSerializer.Serialize(new { session_id = Guid.CreateVersion7() })));
        inner.Emit(CreateClaudeInitUpdate(JsonSerializer.Serialize(new { session_id = "not-a-guid" })));
        inner.Emit(CreateClaudeInitUpdate(JsonSerializer.Serialize(new { tools = Array.Empty<string>() })));
        inner.Emit(
            CreateClaudeInitUpdate(
                JsonSerializer.Serialize(new { session_id = Guid.CreateVersion7() }),
                subtype: "status"
            )
        );
        inner.Complete();

        var updates = await CollectAsync(
            agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
        );

        Assert.Empty(updates);
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task RunStreamingAsync_ClaudeInitBeforeFailure_CapturesSessionAndPreservesFailure()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var inner = new PausableExternalAgent();
        var expectedSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        string? capturedSessionId = null;
        var agent = CreateClaudeAgent(
            inner,
            fixture,
            (providerSessionId, _) =>
            {
                capturedSessionId = providerSessionId;
                return ValueTask.CompletedTask;
            }
        );
        var session = await fixture.CreateSessionAsync(agent);
        await using var enumerator = agent
            .RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
            .GetAsyncEnumerator(Token);
        inner.Emit(CreateClaudeInitUpdate(expectedSessionId));
        inner.Fail(new InvalidOperationException("429 quota exceeded"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal(expectedSessionId.Normalize(), capturedSessionId);
        Assert.Equal("429 quota exceeded", exception.Message);
    }

    [Fact]
    public async Task InvokingAsync_ExistingHistory_ReturnsHistoryThenRequestOnce()
    {
        // Arrange
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.ClaudeCode);
        var agent = new PausableExternalAgent();
        var session = await fixture.CreateSessionAsync(agent);
        await new ConversationHistoryWriter(fixture.Store, fixture.Clock).AppendAsync(
            fixture.ProjectId,
            HistoryTestFixture.ContextId,
            [new ChatMessage(ChatRole.Assistant, "history")],
            Token
        );

        // Act
        var messages = await history.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(agent, session, [new ChatMessage(ChatRole.User, "request")]),
            Token
        );
        await history.InvokedAsync(new ChatHistoryProvider.InvokedContext(agent, session, [], []), Token);

        // Assert
        Assert.Equal(["history", "request"], messages.Select(message => message.Text));
        Assert.Equal(["history", "request"], (await fixture.ReadMessagesAsync()).Select(message => message.Text));
    }

    [Fact]
    public async Task ClaudeHistory_CompletedResponse_SanitizesTransportAndMarksErrorsDisplayOnly()
    {
        // Arrange
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.ClaudeCode);
        var agent = new PausableExternalAgent();
        var session = await fixture.CreateSessionAsync(agent);
        var assistant = new ChatMessage(ChatRole.Assistant, "answer") { RawRepresentation = new object() };
        var retry = new ChatMessage(ChatRole.System, [new ErrorContent("Claude Code API retry 10/10: rate_limit")])
        {
            MessageId = "api-retry-10",
            RawRepresentation = new object(),
            AdditionalProperties = new AdditionalPropertiesDictionary { ["subtype"] = "api_retry" },
        };
        var rateLimit = new ChatMessage(ChatRole.Assistant, [new ErrorContent("rate_limit")])
        {
            MessageId = "synthetic-rate-limit",
            AuthorName = "<synthetic>",
            RawRepresentation = new object(),
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["agentName"] = "claude-code",
                ["type"] = "assistant",
            },
        };

        // Act
        await HistoryTestFixture.RunSdkCallbacksAsync(
            history,
            agent,
            session,
            [],
            [
                assistant,
                retry,
                rateLimit,
                new ChatMessage(ChatRole.System, "{legacy-json}")
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["type"] = "system",
                        ["subtype"] = "future_event",
                    },
                },
            ]
        );

        // Assert
        var stored = await fixture.ReadMessagesAsync();
        Assert.Equal(3, stored.Count);
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(stored[0]));
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(stored[1]));
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(stored[2]));
        Assert.Equal("api_retry", stored[1].AdditionalProperties!["subtype"]?.ToString());
        Assert.Equal(
            "Claude Code API retry 10/10: rate_limit",
            Assert.IsType<ErrorContent>(Assert.Single(stored[1].Contents)).Message
        );
        Assert.Equal(ChatRole.Assistant, stored[2].Role);
        Assert.Equal("<synthetic>", stored[2].AuthorName);
        Assert.Equal("rate_limit", Assert.IsType<ErrorContent>(Assert.Single(stored[2].Contents)).Message);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("author")]
    [InlineData("role")]
    [InlineData("type")]
    [InlineData("content-result")]
    [InlineData("missing-id")]
    public async Task ClaudeHistory_CompletedResponse_PreservesMessageBoundaries(string boundary)
    {
        // Arrange
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.ClaudeCode);
        var agent = new PausableExternalAgent();
        var session = await fixture.CreateSessionAsync(agent);
        var first = new ChatMessage(ChatRole.Assistant, "before")
        {
            MessageId = boundary == "missing-id" ? null : "message-1",
            AuthorName = "claude-code",
            AdditionalProperties = new() { ["type"] = "assistant" },
        };
        var second = first.Clone();
        second.Contents = [new TextContent("after")];
        if (boundary == "id")
            second.MessageId = "message-2";
        if (boundary == "author")
            second.AuthorName = "other-agent";
        if (boundary == "role")
            second.Role = ChatRole.User;
        if (boundary == "type")
            second.AdditionalProperties = new() { ["type"] = "result" };
        if (boundary == "content-result")
            second.Contents[0].AdditionalProperties = new() { ["type"] = "result" };

        // Act
        await HistoryTestFixture.RunSdkCallbacksAsync(history, agent, session, [], [first, second]);

        // Assert
        var saved = await fixture.ReadMessagesAsync();
        Assert.Equal(2, saved.Count);
        Assert.Equal("before", saved[0].Text);
        Assert.Equal("after", saved[^1].Text);
        Assert.Equal(
            boundary is "type" or "content-result" ? "result" : "assistant",
            saved[^1].AdditionalProperties!["type"]?.ToString()
        );
    }

    [Theory]
    [InlineData("tool-result")]
    [InlineData("error")]
    public async Task ClaudeHistory_CompletedResponse_MergesFragmentsAroundInterleavedMessage(string interleaved)
    {
        // Arrange
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var history = fixture.CreateProvider(EngineKind.ClaudeCode);
        var agent = new PausableExternalAgent();
        var session = await fixture.CreateSessionAsync(agent);
        var first = new ChatMessage(ChatRole.Assistant, "before")
        {
            MessageId = "message-1",
            AuthorName = "claude-code",
            AdditionalProperties = new() { ["type"] = "assistant" },
        };
        var second = first.Clone();
        second.Contents = [new TextContent("after")];
        List<ChatMessage> messages =
        [
            first,
            interleaved == "tool-result"
                ? new ChatMessage(ChatRole.User, [new FunctionResultContent("call-1", "result")])
                : new ChatMessage(ChatRole.System, [new ErrorContent("rate limit")])
                {
                    AdditionalProperties = new() { ["type"] = "system", ["subtype"] = "api_retry" },
                },
            second,
        ];

        // Act
        await HistoryTestFixture.RunSdkCallbacksAsync(history, agent, session, [], messages);

        // Assert
        var saved = await fixture.ReadMessagesAsync();
        Assert.Equal(2, saved.Count);
        Assert.Equal("message-1", saved[0].AdditionalProperties!["sourceMessageId"]?.ToString());
        Assert.Equal("beforeafter", saved[0].Text);
        Assert.Equal(messages[1].Role, saved[1].Role);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnEnd_FinalFlushFails_PreservesExecutionFailureOrFailsTurn(bool executionFails)
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var inner = new PausableExternalAgent();
        var agent = HistoryTestFixture.Record(inner, fixture.CreateProvider(EngineKind.Codex));
        var session = await fixture.CreateSessionAsync(agent);
        var turn = fixture.BeginTurn();
        await using (
            var enumerator = agent
                .RunStreamingAsync([new ChatMessage(ChatRole.User, "request")], session, cancellationToken: Token)
                .GetAsyncEnumerator(Token)
        )
        {
            inner.Emit(new AgentResponseUpdate(ChatRole.Assistant, "answer") { MessageId = "item-1" });
            Assert.True(await enumerator.MoveNextAsync());
            if (executionFails)
            {
                inner.Fail(new InvalidOperationException("external failure"));
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    enumerator.MoveNextAsync().AsTask()
                );
                turn.Scope.RecordFailure(failure);
            }
            else
            {
                inner.Complete();
                Assert.False(await enumerator.MoveNextAsync());
            }
        }
        fixture.FailNextContext = true;

        if (executionFails)
            await turn.DisposeAsync();
        else
            await Assert.ThrowsAsync<DbUpdateException>(() => turn.DisposeAsync().AsTask());

        Assert.Empty(await fixture.ReadAsync());
    }

    [Fact]
    public async Task RunAsync_BlockParticipant_UsesParticipantHistoryScopeAndNodeName()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        using var execution = ExecutionTestScopes
            .Scope(
                ExecutionTestScopes.Context(
                    projectId: fixture.ProjectId,
                    contextId: HistoryTestFixture.ContextId,
                    userId: HistoryTestFixture.UserId,
                    runtimeType: AgentRuntimeType.Agentflow,
                    conversationId: fixture.ConversationId
                )
            )
            .Push();
        var externalAgent = HistoryTestFixture.Record(
            new PausableExternalAgent(),
            fixture.CreateProvider(EngineKind.Codex)
        );
        var agentflowId = Guid.CreateVersion7();
        var scopedAgent = new AgentflowNodeScopedAgent(
            externalAgent,
            "group.participant",
            "Participant",
            instructions: null,
            new AgentflowAgentSessionScope(
                fixture.SessionState,
                fixture.ProjectId,
                HistoryTestFixture.ContextId,
                taskId: null
            ),
            agentflowId: agentflowId,
            agentId: Guid.CreateVersion7(),
            historyNodeId: "participant"
        );

        var response = await scopedAgent.RunAsync(
            [new ChatMessage(ChatRole.User, "request")],
            cancellationToken: Token
        );

        Assert.Equal("non-streaming answer", Assert.Single(response.Messages).Text);
        var rows = await fixture.ReadAsync();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal($"agentflow:{agentflowId:N}:node:participant", row.HistoryScope));
        var answer = Assert.Single(rows, row => row.GetText() == "non-streaming answer").ToChatMessage()!;
        Assert.Equal("Participant", answer.AdditionalProperties!["nodeName"]?.ToString());
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static AIAgent CreateClaudeAgent(
        AIAgent innerAgent,
        HistoryTestFixture fixture,
        Func<string, CancellationToken, ValueTask> onProviderSessionStartedAsync
    ) =>
        HistoryTestFixture.Record(
            new ClaudeCodeProviderSessionTrackingAgent(innerAgent, onProviderSessionStartedAsync),
            fixture.CreateProvider(EngineKind.ClaudeCode)
        );

    private static ChatMessage CreateClaudeSystemMessage(string subtype, Guid sessionId)
    {
        var data = new Dictionary<string, object>
        {
            ["type"] = "system",
            ["session_id"] = sessionId.ToString(),
            ["uuid"] = Guid.CreateVersion7().ToString(),
        };
        var raw = new ClaudeCodeSdk.Types.SystemMessage
        {
            Id = data["uuid"].ToString()!,
            Subtype = subtype,
            SessionId = sessionId.ToString(),
            Data = data,
        };
        return new ChatMessage(ChatRole.System, JsonSerializer.Serialize(data))
        {
            AuthorName = "claude-code",
            MessageId = raw.Id,
            RawRepresentation = raw,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["type"] = "system",
                ["subtype"] = subtype,
                ["session_id"] = raw.SessionId,
                ["systemData"] = data,
            },
        };
    }

    private static AgentResponseUpdate CreateClaudeInitUpdate(Guid sessionId)
    {
        var message = CreateClaudeSystemMessage("init", sessionId);
        return new AgentResponseUpdate(ChatRole.System, message.Contents)
        {
            AdditionalProperties = message.AdditionalProperties,
            RawRepresentation = message.RawRepresentation,
        };
    }

    private static AgentResponseUpdate CreateClaudeInitUpdate(string content, string subtype = "init") =>
        new(ChatRole.System, content)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["subtype"] = subtype },
        };

    private static ChatMessage CreateClaudeInitMessage(Guid sessionId) => CreateClaudeSystemMessage("init", sessionId);

    private static async Task<List<AgentResponseUpdate>> CollectAsync(IAsyncEnumerable<AgentResponseUpdate> updates)
    {
        var result = new List<AgentResponseUpdate>();
        await foreach (var update in updates)
            result.Add(update);
        return result;
    }

    private sealed class PausableExternalAgent : AIAgent
    {
        private readonly Channel<AgentResponseUpdate> _updates = Channel.CreateUnbounded<AgentResponseUpdate>();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public bool StreamDisposed { get; private set; }

        public IReadOnlyList<ChatMessage> NonStreamingMessages { get; init; } =
        [new ChatMessage(ChatRole.Assistant, "non-streaming answer")];

        public void Emit(AgentResponseUpdate update) => Assert.True(_updates.Writer.TryWrite(update));

        public void Complete() => Assert.True(_updates.Writer.TryComplete());

        public void Fail(Exception exception) => Assert.True(_updates.Writer.TryComplete(exception));

        protected override string? IdCore => "external";

        public override string? Name => "External";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ExternalSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new ExternalSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => Task.FromResult(new AgentResponse(NonStreamingMessages.ToList()));

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            _started.TrySetResult();
            try
            {
                await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
                    yield return update;
            }
            finally
            {
                StreamDisposed = true;
            }
        }

        private sealed class ExternalSession : AgentSession;
    }
}
