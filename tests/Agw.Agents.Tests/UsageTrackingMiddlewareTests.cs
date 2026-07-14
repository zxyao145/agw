using System.Runtime.CompilerServices;

using Agw.Agents.Execution.Agents.Middleware;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Projects;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class UsageTrackingMiddlewareTests
{
    [Fact]
    public async Task TrackRunMiddleware_ResponseHasUsage_RecordsUsage()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(new UsageChatClient
        {
            ResponseUsage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 20,
                TotalTokenCount = 30,
                CachedInputTokenCount = 4,
                ReasoningTokenCount = 5
            }
        });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await middleware.TrackRunMiddleware(
            [new ChatMessage(ChatRole.User, "hello")],
            session,
            options: null,
            agent,
            TestContext.Current.CancellationToken);

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
    public async Task TrackStreamingMiddleware_MultipleUsageContents_RecordsCombinedUsage()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(new UsageChatClient
        {
            StreamingUsage =
            [
                new UsageDetails
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 20,
                    TotalTokenCount = 30,
                    CachedInputTokenCount = 4,
                    ReasoningTokenCount = 5
                },
                new UsageDetails
                {
                    InputTokenCount = 1,
                    OutputTokenCount = 2,
                    TotalTokenCount = 3,
                    CachedInputTokenCount = 6,
                    ReasoningTokenCount = 7
                }
            ]
        });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await foreach (var _ in middleware.TrackStreamingMiddleware(
                           [new ChatMessage(ChatRole.User, "hello")],
                           session,
                           options: null,
                           agent,
                           TestContext.Current.CancellationToken))
        {
        }

        var recorded = Assert.Single(recorder.Entries);
        Assert.Equal("agent", recorded.AgentName);
        Assert.Equal(11, recorded.Usage.InputTokenCount);
        Assert.Equal(22, recorded.Usage.OutputTokenCount);
        Assert.Equal(33, recorded.Usage.TotalTokenCount);
        Assert.Equal(10, recorded.Usage.CachedInputTokenCount);
        Assert.Equal(12, recorded.Usage.ReasoningTokenCount);
    }

    [Fact]
    public async Task TrackStreamingMiddleware_InnerAgentFailsAfterUsage_RecordsObservedUsage()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(new UsageChatClient
        {
            StreamingUsage = [new UsageDetails { TotalTokenCount = 9 }],
            FailStreamingAfterUsage = true
        });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in middleware.TrackStreamingMiddleware(
                               [new ChatMessage(ChatRole.User, "hello")],
                               session,
                               options: null,
                               agent,
                               TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal(9, Assert.Single(recorder.Entries).Usage.TotalTokenCount);
    }

    [Fact]
    public async Task TrackRunMiddleware_RecorderFails_ReturnsAgentResponse()
    {
        var recorder = new CapturingUsageRecorder { ThrowOnAdd = true };
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(new UsageChatClient
        {
            ResponseUsage = new UsageDetails { TotalTokenCount = 3 }
        });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await middleware.TrackRunMiddleware(
            [new ChatMessage(ChatRole.User, "hello")],
            session,
            options: null,
            agent,
            TestContext.Current.CancellationToken);

        Assert.Equal("response", response.Text);
    }

    [Fact]
    public async Task TrackRunMiddleware_ResponseHasNoUsage_DoesNotRecord()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(new UsageChatClient());
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await middleware.TrackRunMiddleware(
            [new ChatMessage(ChatRole.User, "hello")],
            session,
            options: null,
            agent,
            TestContext.Current.CancellationToken);

        Assert.Empty(recorder.Entries);
    }

    [Fact]
    public async Task TrackRunMiddleware_ResponseHasZeroUsage_RecordsUsage()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(new UsageChatClient { ResponseUsage = new UsageDetails() });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await middleware.TrackRunMiddleware(
            [new ChatMessage(ChatRole.User, "hello")],
            session,
            options: null,
            agent,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ProjectContextUsage(), Assert.Single(recorder.Entries).Usage);
    }

    [Fact]
    public async Task TrackRunMiddleware_AgentHasNoName_RecordsUnknownAgentName()
    {
        var recorder = new CapturingUsageRecorder();
        var middleware = CreateMiddleware(recorder);
        var agent = CreateAgent(
            new UsageChatClient { ResponseUsage = new UsageDetails { TotalTokenCount = 3 } },
            name: null);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await middleware.TrackRunMiddleware(
            [new ChatMessage(ChatRole.User, "hello")],
            session,
            options: null,
            agent,
            TestContext.Current.CancellationToken);

        Assert.Equal("$unknown", Assert.Single(recorder.Entries).AgentName);
    }

    private static UsageTrackingMiddleware CreateMiddleware(IAgentUsageRecorder recorder) =>
        new(
            new StubProviderSessionState(),
            recorder,
            NullLogger<UsageTrackingMiddleware>.Instance);

    private static AIAgent CreateAgent(IChatClient chatClient, string? name = "agent") =>
        new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = "agent-id",
                Name = name
            });

    private sealed class StubProviderSessionState : IProviderSessionState
    {
        public void InitializeSessionState(AgentSession session, string contextId, Guid projectId)
        {
        }

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope)
        {
        }

        public bool TryGetProjectContext(AgentSession session, out Guid projectId, out string contextId)
        {
            projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            contextId = "context-1";
            return true;
        }
    }

    private sealed class CapturingUsageRecorder : IAgentUsageRecorder
    {
        public List<Entry> Entries { get; } = [];

        public bool ThrowOnAdd { get; init; }

        public Task AddAsync(
            Guid projectId,
            string contextId,
            string agentName,
            ProjectContextUsage usage,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnAdd)
            {
                throw new InvalidOperationException("Recorder failed.");
            }

            Entries.Add(new Entry(projectId, contextId, agentName, usage));
            return Task.CompletedTask;
        }
    }

    private sealed record Entry(
        Guid ProjectId,
        string ContextId,
        string AgentName,
        ProjectContextUsage Usage);

    private sealed class UsageChatClient : IChatClient
    {
        public UsageDetails? ResponseUsage { get; init; }

        public IReadOnlyList<UsageDetails> StreamingUsage { get; init; } = [];

        public bool FailStreamingAfterUsage { get; init; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "response")])
            {
                Usage = ResponseUsage
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var usage in StreamingUsage)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new UsageContent(usage)]
                };
            }

            if (FailStreamingAfterUsage)
            {
                throw new InvalidOperationException("Agent failed.");
            }

            await Task.CompletedTask;
        }
    }
}
