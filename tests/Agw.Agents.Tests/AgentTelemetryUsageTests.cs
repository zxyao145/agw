using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agents.Middleware.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentTelemetryUsageTests
{
    [Fact]
    public async Task RunAsync_ResponseHasUsage_RecordsUsage()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(
            new UsageChatClient
            {
                ResponseUsage = new UsageDetails
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 20,
                    TotalTokenCount = 30,
                    CachedInputTokenCount = 4,
                    ReasoningTokenCount = 5,
                },
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await middleware.RunAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            session,
            options: null,
            agent,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("response", response.Text);
        var recorded = Assert.Single(recorder.Entries);
        Assert.Equal("agent", recorded.AgentName);
        Assert.Equal(10, recorded.Usage.InputTokenCount);
        Assert.Equal(20, recorded.Usage.OutputTokenCount);
        Assert.Equal(30, recorded.Usage.TotalTokenCount);
        Assert.Equal(4, recorded.Usage.CachedInputTokenCount);
        Assert.Equal(5, recorded.Usage.ReasoningTokenCount);
    }

    [Fact]
    public async Task RunStreamingAsync_MultipleUsageContents_RecordsCombinedUsage()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(
            new UsageChatClient
            {
                StreamingUsage =
                [
                    new UsageDetails
                    {
                        InputTokenCount = 10,
                        OutputTokenCount = 20,
                        TotalTokenCount = 30,
                        CachedInputTokenCount = 4,
                        ReasoningTokenCount = 5,
                    },
                    new UsageDetails
                    {
                        InputTokenCount = 1,
                        OutputTokenCount = 2,
                        TotalTokenCount = 3,
                        CachedInputTokenCount = 6,
                        ReasoningTokenCount = 7,
                    },
                ],
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await foreach (
            var _ in middleware.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                session,
                options: null,
                agent,
                TestContext.Current.CancellationToken
            )
        ) { }

        var recorded = Assert.Single(recorder.Entries);
        Assert.Equal("agent", recorded.AgentName);
        Assert.Equal(11, recorded.Usage.InputTokenCount);
        Assert.Equal(22, recorded.Usage.OutputTokenCount);
        Assert.Equal(33, recorded.Usage.TotalTokenCount);
        Assert.Equal(10, recorded.Usage.CachedInputTokenCount);
        Assert.Equal(12, recorded.Usage.ReasoningTokenCount);
    }

    [Fact]
    public async Task RunStreamingAsync_InnerAgentFailsAfterUsage_RecordsObservedUsage()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(
            new UsageChatClient
            {
                StreamingUsage = [new UsageDetails { TotalTokenCount = 9 }],
                FailStreamingAfterUsage = true,
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (
                var _ in middleware.RunStreamingAsync(
                    [new ChatMessage(ChatRole.User, "hello")],
                    session,
                    options: null,
                    agent,
                    TestContext.Current.CancellationToken
                )
            ) { }
        });

        Assert.Equal(9, Assert.Single(recorder.Entries).Usage.TotalTokenCount);
    }

    [Fact]
    public async Task RunAsync_RecorderFails_ReturnsAgentResponse()
    {
        var recorder = new CapturingUsageRecorder { ThrowOnAdd = true };
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(new UsageChatClient { ResponseUsage = new UsageDetails { TotalTokenCount = 3 } });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await middleware.RunAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            session,
            options: null,
            agent,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("response", response.Text);
    }

    [Fact]
    public async Task RunAsync_ResponseHasNoUsage_DoesNotRecord()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(new UsageChatClient());
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await middleware.RunAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            session,
            options: null,
            agent,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(recorder.Entries);
    }

    [Fact]
    public async Task RunAsync_ResponseHasZeroUsage_RecordsUsage()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(new UsageChatClient { ResponseUsage = new UsageDetails() });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await middleware.RunAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            session,
            options: null,
            agent,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new ProjectContextUsage(), Assert.Single(recorder.Entries).Usage);
    }

    [Fact]
    public async Task RunAsync_AgentHasNoName_RecordsUnknownAgentName()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(
            new UsageChatClient { ResponseUsage = new UsageDetails { TotalTokenCount = 3 } },
            name: null
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await middleware.RunAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            session,
            options: null,
            agent,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("$unknown", Assert.Single(recorder.Entries).AgentName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunStreamingAsync_InterruptedAfterUsage_RecordsOnlyObservedUsageWithoutCompletionLog(bool cancel)
    {
        var recorder = new CapturingUsageRecorder();
        var logger = new RecordingLogger();
        var middleware = new AgentTelemetryMiddleware(new StubProviderSessionState(), recorder, logger);
        var agent = CreateAgent(
            new UsageChatClient
            {
                StreamingUsage = [new UsageDetails { TotalTokenCount = 9 }, new UsageDetails { TotalTokenCount = 40 }],
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var enumerator = middleware
            .RunStreamingAsync([], session, null, agent, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        Assert.Equal(9, Assert.Single(recorder.Entries).Usage.TotalTokenCount);
        Assert.Equal(CancellationToken.None, recorder.LastCancellationToken);
        Assert.DoesNotContain(
            logger.Messages,
            message => message.StartsWith("Executed agent", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_Success_RecordsUsageBeforeCompletionLog(bool streaming)
    {
        var logger = new RecordingLogger();
        var recorder = new CapturingUsageRecorder { OnAdd = () => logger.Messages.Add("usage") };
        var middleware = new AgentTelemetryMiddleware(new StubProviderSessionState(), recorder, logger);
        var agent = CreateAgent(
            new UsageChatClient
            {
                ResponseUsage = new UsageDetails { TotalTokenCount = 9 },
                StreamingUsage = [new UsageDetails { TotalTokenCount = 9 }],
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        if (streaming)
        {
            await foreach (
                var _ in middleware.RunStreamingAsync([], session, null, agent, TestContext.Current.CancellationToken)
            ) { }
        }
        else
        {
            await middleware.RunAsync([], session, null, agent, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["Executing agent agent", "usage", "Executed agent agent"], logger.Messages);
        Assert.Single(recorder.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_MissingProjectContext_DoesNotRecord(bool missingSession)
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = new AgentTelemetryMiddleware(
            new StubProviderSessionState { HasProjectContext = false },
            recorder,
            NullLogger<AgentTelemetryMiddleware>.Instance
        );
        var agent = CreateAgent(new UsageChatClient { ResponseUsage = new UsageDetails { TotalTokenCount = 9 } });
        var session = missingSession ? null : await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await middleware.RunAsync([], session, null, agent, TestContext.Current.CancellationToken);

        Assert.Equal("response", response.Text);
        Assert.Empty(recorder.Entries);
    }

    [Fact]
    public async Task RunStreamingAsync_InterleavedExecutions_RecordsUsageIndependently()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var firstAgent = CreateAgent(
            new UsageChatClient { StreamingUsage = [new UsageDetails { TotalTokenCount = 3 }] },
            "first"
        );
        var secondAgent = CreateAgent(
            new UsageChatClient { StreamingUsage = [new UsageDetails { TotalTokenCount = 7 }] },
            "second"
        );
        var firstSession = await firstAgent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var secondSession = await secondAgent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await using var first = middleware
            .RunStreamingAsync([], firstSession, null, firstAgent, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var second = middleware
            .RunStreamingAsync([], secondSession, null, secondAgent, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await first.MoveNextAsync());
        Assert.True(await second.MoveNextAsync());
        Assert.False(await first.MoveNextAsync());
        Assert.False(await second.MoveNextAsync());

        Assert.Collection(
            recorder.Entries,
            entry =>
            {
                Assert.Equal("first", entry.AgentName);
                Assert.Equal(3, entry.Usage.TotalTokenCount);
            },
            entry =>
            {
                Assert.Equal("second", entry.AgentName);
                Assert.Equal(7, entry.Usage.TotalTokenCount);
            }
        );
    }

    private sealed class RecordingLogger : ILogger<AgentTelemetryMiddleware>
    {
        public List<string> Messages { get; } = [];

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Information)
                Messages.Add(formatter(state, exception));
        }
    }

    private static AgentTelemetryMiddleware CreateMiddleware(IAgentUsageRecorder recorder) =>
        new(new StubProviderSessionState(), recorder, NullLogger<AgentTelemetryMiddleware>.Instance);

    private static AIAgent CreateAgent(IChatClient chatClient, string? name = "agent") =>
        new ChatClientAgent(chatClient, new ChatClientAgentOptions { Id = "agent-id", Name = name });

    private sealed class StubProviderSessionState : IProviderSessionState
    {
        public bool HasProjectContext { get; init; } = true;

        public void InitializeSessionState(AgentSession session, string contextId, Guid projectId) { }

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope
        ) { }

        public bool TryGetProjectContext(AgentSession session, out Guid projectId, out string contextId)
        {
            projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            contextId = "context-1";
            return HasProjectContext;
        }
    }

    private sealed class CapturingUsageRecorder : IAgentUsageRecorder
    {
        public List<Entry> Entries { get; } = [];

        public bool ThrowOnAdd { get; init; }
        public Action? OnAdd { get; init; }
        public CancellationToken LastCancellationToken { get; private set; }

        public Task AddAsync(
            Guid projectId,
            string contextId,
            string agentName,
            ProjectContextUsage usage,
            CancellationToken cancellationToken = default
        )
        {
            LastCancellationToken = cancellationToken;
            OnAdd?.Invoke();
            if (ThrowOnAdd)
            {
                throw new InvalidOperationException("Recorder failed.");
            }

            Entries.Add(new Entry(projectId, contextId, agentName, usage));
            return Task.CompletedTask;
        }
    }

    private sealed record Entry(Guid ProjectId, string ContextId, string AgentName, ProjectContextUsage Usage);

    private sealed class UsageChatClient : IChatClient
    {
        public UsageDetails? ResponseUsage { get; init; }

        public IReadOnlyList<UsageDetails> StreamingUsage { get; init; } = [];

        public bool FailStreamingAfterUsage { get; init; }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new ChatResponse([new ChatMessage(ChatRole.Assistant, "response")]) { Usage = ResponseUsage }
            );

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            foreach (var usage in StreamingUsage)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new UsageContent(usage)] };
            }

            if (FailStreamingAfterUsage)
            {
                throw new InvalidOperationException("Agent failed.");
            }

            await Task.CompletedTask;
        }
    }
}
