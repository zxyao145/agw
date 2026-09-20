using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Agw.Agents.Execution.Agentflows.Context;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using Agw.Agents.Execution.Agents.History;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class ExternalAgentChatHistoryAgentTests
{
    [Fact]
    public async Task RunStreamingAsync_NoResponse_DoesNotWriteResponseHistory()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var firstUpdate = enumerator.MoveNextAsync().AsTask();
        await innerAgent.Started.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Empty(provider.Calls);
        Assert.False(firstUpdate.IsCompleted);

        innerAgent.Complete();
        Assert.False(await firstUpdate);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenTwentyMessagesArrive_FlushesOneOrderedBatch()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        for (var index = 0; index < 20; index++)
        {
            innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, $"update-{index}"));
        }

        for (var index = 0; index < 20; index++)
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        var responseCall = Assert.Single(provider.Calls, call => call.ResponseMessages.Count > 0);
        Assert.Empty(responseCall.RequestMessages);
        Assert.Equal(
            Enumerable.Range(0, 20).Select(index => $"update-{index}"),
            responseCall.ResponseMessages.Select(message => message.Text)
        );

        innerAgent.Complete();
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenExternalAgentPauses_FlushesAfterOneSecond()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "update"));
        Assert.True(await enumerator.MoveNextAsync());

        var pendingUpdate = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        Assert.Empty(provider.Calls);
        await provider.WaitForCallCountAsync(1, TimeSpan.FromSeconds(3));

        var responseCall = provider.Calls[0];
        Assert.Equal("update", Assert.Single(responseCall.ResponseMessages).Text);
        Assert.False(pendingUpdate.IsCompleted);

        innerAgent.Complete();
        Assert.False(await pendingUpdate);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task RunStreamingAsync_OnNormalCompletion_FlushesRemainderWithoutTurnEndDuplicate()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "first"));
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "second"));
        innerAgent.Complete();

        var updates = await CollectAsync(
            agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(["first", "second"], updates.Select(update => update.Text));
        Assert.Single(provider.Calls);
        Assert.Equal(["first", "second"], provider.Calls[0].ResponseMessages.Select(message => message.Text));
    }

    [Fact]
    public async Task RunStreamingAsync_WhenCancelled_FlushesProducedMessages()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: cancellationSource.Token
            )
            .GetAsyncEnumerator(cancellationSource.Token);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "before cancellation"));
        Assert.True(await enumerator.MoveNextAsync());

        var pendingUpdate = enumerator.MoveNextAsync().AsTask();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingUpdate);
        Assert.Equal("before cancellation", Assert.Single(provider.Calls[0].ResponseMessages).Text);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenInnerAgentFails_FlushesProducedMessagesAndPreservesFailure()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "before failure"));
        Assert.True(await enumerator.MoveNextAsync());
        innerAgent.Fail(new InvalidOperationException("external failure"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal("external failure", exception.Message);
        Assert.Equal("before failure", Assert.Single(provider.Calls[0].ResponseMessages).Text);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenConsumerDisposesEarly_FlushesProducedMessages()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "before disposal"));
        Assert.True(await enumerator.MoveNextAsync());

        await enumerator.DisposeAsync();

        Assert.Equal(
            "before disposal",
            Assert.Single(Assert.Single(provider.Calls, call => call.ResponseMessages.Count > 0).ResponseMessages).Text
        );
        Assert.True(innerAgent.StreamDisposed);
    }

    [Fact]
    public async Task RunStreamingAsync_DisplayOnlyEvents_AreMarkedAndEmptyControlEventsAreSkipped()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        innerAgent.Emit(
            new AgentResponseUpdate(ChatRole.System, "Todo list")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = "todo" },
            }
        );
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "answer"));
        innerAgent.Emit(new AgentResponseUpdate { Role = ChatRole.System });
        innerAgent.Complete();

        await CollectAsync(
            agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        var messages = Assert.Single(provider.Calls, call => call.ResponseMessages.Count > 0).ResponseMessages;
        Assert.Equal(2, messages.Count);
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(messages[0]));
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(messages[1]));
        Assert.Equal(["Todo list", "answer"], messages.Select(message => message.Text));
    }

    [Fact]
    public async Task RunStreamingAsync_ToolResponse_IsMarkedDisplayOnly()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        innerAgent.Emit(
            new AgentResponseUpdate(
                ChatRole.Tool,
                [new FunctionResultContent("tool-1", new Dictionary<string, object> { ["detail"] = "boom" })]
            )
        );
        innerAgent.Complete();

        await CollectAsync(
            agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        var message = Assert.Single(
            Assert.Single(provider.Calls, call => call.ResponseMessages.Count > 0).ResponseMessages
        );
        Assert.Equal(ChatRole.Tool, message.Role);
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
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
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
            cancellationToken: TestContext.Current.CancellationToken
        );
        var updates = await CollectAsync(
            agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        var history = new RecordingChatHistoryProvider();
        await new ClaudeCodeChatHistoryProvider(history).InvokedAsync(
            new ChatHistoryProvider.InvokedContext(agent, session, [], [notification, retry, answer]),
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(2, Assert.Single(history.Calls).ResponseMessages.Count);
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
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var capturedSessionIds = new List<string>();
        var agent = CreateAgent(
            innerAgent,
            provider,
            (providerSessionId, _) =>
            {
                capturedSessionIds.Add(providerSessionId);
                return ValueTask.CompletedTask;
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var expectedSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        innerAgent.Emit(CreateClaudeInitUpdate(expectedSessionId));
        innerAgent.Emit(CreateClaudeInitUpdate(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        innerAgent.Complete();

        var updates = await CollectAsync(
            agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Empty(updates);
        Assert.Equal(expectedSessionId.Normalize(), Assert.Single(capturedSessionIds));
        Assert.All(provider.Calls, call => Assert.Empty(call.ResponseMessages));
    }

    [Fact]
    public async Task RunAsync_ClaudeInit_CapturesProviderSession()
    {
        var provider = new RecordingChatHistoryProvider();
        var expectedSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var innerAgent = new PausableExternalAgent
        {
            NonStreamingMessages =
            [
                CreateClaudeInitMessage(expectedSessionId),
                new ChatMessage(ChatRole.Assistant, "non-streaming answer"),
            ],
        };
        string? capturedSessionId = null;
        var agent = CreateAgent(
            innerAgent,
            provider,
            (providerSessionId, _) =>
            {
                capturedSessionId = providerSessionId;
                return ValueTask.CompletedTask;
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "request")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(expectedSessionId.Normalize(), capturedSessionId);
        Assert.Single(response.Messages);
        Assert.Single(Assert.Single(provider.Calls, call => call.ResponseMessages.Count > 0).ResponseMessages);
    }

    [Fact]
    public async Task RunStreamingAsync_InvalidClaudeInit_DoesNotCaptureOrChangeUpdates()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var callbackCount = 0;
        var agent = CreateAgent(
            innerAgent,
            provider,
            (_, _) =>
            {
                callbackCount++;
                return ValueTask.CompletedTask;
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        innerAgent.Emit(CreateClaudeInitUpdate("{bad json"));
        innerAgent.Emit(CreateClaudeInitUpdate(JsonSerializer.Serialize(new { session_id = "not-a-guid" })));
        innerAgent.Emit(CreateClaudeInitUpdate(JsonSerializer.Serialize(new { tools = Array.Empty<string>() })));
        innerAgent.Emit(
            CreateClaudeInitUpdate(
                JsonSerializer.Serialize(new { session_id = Guid.CreateVersion7() }),
                subtype: "status"
            )
        );
        innerAgent.Complete();

        var updates = await CollectAsync(
            agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Empty(updates);
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task RunStreamingAsync_ClaudeInitBeforeFailure_CapturesSessionAndPreservesFailure()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var expectedSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        string? capturedSessionId = null;
        var agent = CreateAgent(
            innerAgent,
            provider,
            (providerSessionId, _) =>
            {
                capturedSessionId = providerSessionId;
                return ValueTask.CompletedTask;
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        innerAgent.Emit(CreateClaudeInitUpdate(expectedSessionId));
        innerAgent.Fail(new InvalidOperationException("429 quota exceeded"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal(expectedSessionId.Normalize(), capturedSessionId);
        Assert.Equal("429 quota exceeded", exception.Message);
    }

    [Fact]
    public async Task ClaudeCodeChatHistoryProvider_Invoking_DelegatesWithoutDuplicatingRequest()
    {
        // Arrange
        var innerProvider = new RecordingChatHistoryProvider
        {
            ProvidedMessages = [new ChatMessage(ChatRole.Assistant, "history")],
        };
        var provider = new ClaudeCodeChatHistoryProvider(innerProvider);
        var agent = new PausableExternalAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var request = new ChatMessage(ChatRole.User, "request");

        // Act
        var messages = await provider.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(agent, session, [request]),
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(["history", "request"], messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ClaudeCodeChatHistoryProvider_Invoked_SanitizesCompletedResponseBeforeDelegating()
    {
        // Arrange
        var innerProvider = new RecordingChatHistoryProvider();
        var provider = new ClaudeCodeChatHistoryProvider(innerProvider);
        var agent = new PausableExternalAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var request = new ChatMessage(ChatRole.User, "request");
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
        await provider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(
                agent,
                session,
                [request],
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
            ),
            TestContext.Current.CancellationToken
        );

        // Assert
        var call = Assert.Single(innerProvider.Calls);
        Assert.Empty(call.RequestMessages);
        Assert.Equal(3, call.ResponseMessages.Count);
        Assert.All(call.ResponseMessages, message => Assert.Null(message.RawRepresentation));
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(call.ResponseMessages[0]));
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(call.ResponseMessages[1]));
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(call.ResponseMessages[2]));
        Assert.Equal("api_retry", call.ResponseMessages[1].AdditionalProperties!["subtype"]?.ToString());
        Assert.Equal(
            "Claude Code API retry 10/10: rate_limit",
            Assert.IsType<ErrorContent>(Assert.Single(call.ResponseMessages[1].Contents)).Message
        );
        Assert.Equal(ChatRole.Assistant, call.ResponseMessages[2].Role);
        Assert.Equal("<synthetic>", call.ResponseMessages[2].AuthorName);
        Assert.Equal(
            "rate_limit",
            Assert.IsType<ErrorContent>(Assert.Single(call.ResponseMessages[2].Contents)).Message
        );
    }

    [Fact]
    public async Task ClaudeCodeChatHistoryProvider_Invoked_ExcludesInjectedContextFromRequestHistory()
    {
        var innerProvider = new RecordingChatHistoryProvider();
        var provider = new ClaudeCodeChatHistoryProvider(innerProvider);
        var agent = new PausableExternalAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var memory = new ChatMessage(ChatRole.User, "memory context").WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            "UserMemoryProvider"
        );
        var request = new ChatMessage(ChatRole.User, "request");

        await provider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(agent, session, [memory, request], []),
            TestContext.Current.CancellationToken
        );

        Assert.Empty(Assert.Single(innerProvider.Calls).RequestMessages);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenFinalPersistenceFailsDuringExecutionFailure_PreservesExecutionFailure()
    {
        var provider = new RecordingChatHistoryProvider { FailureCallNumber = 1 };
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "before failure"));
        Assert.True(await enumerator.MoveNextAsync());
        innerAgent.Fail(new InvalidOperationException("external failure"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal("external failure", exception.Message);
        Assert.Equal(1, provider.AttemptCount);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenFinalPersistenceFailsWithoutExecutionFailure_FailsTurn()
    {
        var provider = new RecordingChatHistoryProvider { FailureCallNumber = 1 };
        var innerAgent = new PausableExternalAgent();
        var agent = CreateAgent(innerAgent, provider);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var enumerator = agent
            .RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "request")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        innerAgent.Emit(new AgentResponseUpdate(ChatRole.Assistant, "answer"));
        Assert.True(await enumerator.MoveNextAsync());
        innerAgent.Complete();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal("persistence failure", exception.Message);
        Assert.Equal(1, provider.AttemptCount);
    }

    [Fact]
    public async Task RunStreamingAsync_BlockParticipant_UsesParticipantHistoryScopeAndNodeName()
    {
        var provider = new RecordingChatHistoryProvider();
        var innerAgent = new PausableExternalAgent();
        var externalAgent = CreateAgent(innerAgent, provider);
        var providerSessionState = new CapturingProviderSessionState();
        var agentflowId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var scopedAgent = new AgentflowNodeScopedAgent(
            externalAgent,
            "group.participant",
            "Participant",
            instructions: null,
            new AgentflowAgentSessionScope(providerSessionState, projectId, "context-1", taskId: null),
            agentflowId: agentflowId,
            agentId: Guid.CreateVersion7(),
            historyNodeId: "participant"
        );
        var response = await scopedAgent.RunAsync(
            [new ChatMessage(ChatRole.User, "request")],
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("non-streaming answer", Assert.Single(response.Messages).Text);
        Assert.Equal(projectId, providerSessionState.ProjectId);
        Assert.Equal("context-1", providerSessionState.ContextId);
        Assert.Equal($"agentflow:{agentflowId:N}:node:participant", providerSessionState.HistoryScope);
        Assert.Equal("Participant", providerSessionState.NodeName);
        Assert.All(provider.Calls, call => Assert.Same(providerSessionState.Session, call.Session));
    }

    private static AIAgent CreateAgent(
        AIAgent innerAgent,
        ChatHistoryProvider provider,
        Func<string, CancellationToken, ValueTask>? onProviderSessionStartedAsync = null
    )
    {
        if (onProviderSessionStartedAsync != null)
        {
            innerAgent = new ClaudeCodeProviderSessionTrackingAgent(innerAgent, onProviderSessionStartedAsync);
        }
        AIAgent agent = new ExternalAgentChatHistoryAgent(
            innerAgent,
            provider,
            TimeProvider.System,
            NullLogger<ExternalAgentChatHistoryAgent>.Instance
        );
        return agent;
    }

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
        {
            result.Add(update);
        }

        return result;
    }

    private sealed class RecordingChatHistoryProvider : ChatHistoryProvider
    {
        private readonly SemaphoreSlim _callsChanged = new(0);

        public List<HistoryCall> Calls { get; } = [];

        public int AttemptCount { get; private set; }

        public int? FailureCallNumber { get; init; }

        public IReadOnlyList<ChatMessage> ProvidedMessages { get; init; } = [];

        public async Task WaitForCallCountAsync(int count, TimeSpan timeout)
        {
            using var cancellationSource = new CancellationTokenSource(timeout);
            while (Calls.Count < count)
            {
                await _callsChanged.WaitAsync(cancellationSource.Token);
            }
        }

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<IEnumerable<ChatMessage>>(ProvidedMessages);

        protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken)
        {
            AttemptCount++;
            if (FailureCallNumber == AttemptCount)
            {
                throw new InvalidOperationException("persistence failure");
            }

            Calls.Add(
                new HistoryCall(
                    context.Agent.Name,
                    context.Session,
                    context.RequestMessages.ToList(),
                    context.ResponseMessages?.ToList() ?? []
                )
            );
            _callsChanged.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record HistoryCall(
        string? AgentName,
        AgentSession? Session,
        IReadOnlyList<ChatMessage> RequestMessages,
        IReadOnlyList<ChatMessage> ResponseMessages
    );

    private sealed class CapturingProviderSessionState : IProviderSessionState
    {
        public AgentSession? Session { get; private set; }

        public Guid ProjectId { get; private set; }

        public string? ContextId { get; private set; }

        public string? HistoryScope { get; private set; }

        public string? NodeName { get; private set; }

        public void InitializeSessionState(AgentSession session, string contextId, Guid projectId)
        {
            Session = session;
            ProjectId = projectId;
            ContextId = contextId;
        }

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope
        ) => InitializeSessionState(session, contextId, projectId, historyScope, nodeName: null);

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope,
            string? nodeName
        )
        {
            InitializeSessionState(session, contextId, projectId);
            HistoryScope = historyScope;
            NodeName = nodeName;
        }
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
                {
                    yield return update;
                }
            }
            finally
            {
                StreamDisposed = true;
            }
        }

        private sealed class ExternalSession : AgentSession;
    }
}
