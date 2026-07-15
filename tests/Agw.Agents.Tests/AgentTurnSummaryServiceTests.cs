using System.Runtime.CompilerServices;

using Agw.Agents.Execution.Summaries;
using Agw.Shared;
using Agw.Shared.Contracts.Projects;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentTurnSummaryServiceTests
{
    [Fact]
    public async Task CreateResultAsync_Success_ReturnsAndPersistsTextResultWithUsage()
    {
        var projectId = Guid.NewGuid();
        var modelProviderId = Guid.NewGuid();
        var client = new RecordingChatClient(new ChatResponse([
            new ChatMessage(ChatRole.Assistant, "  ## 完成\n\n- 已支持 **Markdown**。  ")
        ])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 11,
                OutputTokenCount = 7,
                TotalTokenCount = 18,
            }
        });
        var writer = new RecordingConversationHistoryWriter();
        var usageRecorder = new RecordingUsageRecorder();
        var clientFactory = new StubSummaryChatClientFactory(client);
        var service = new AgentTurnSummaryService(
            clientFactory,
            writer,
            usageRecorder,
            NullLogger<AgentTurnSummaryService>.Instance);

        var result = await service.CreateResultAsync(
            modelProviderId,
            [
                new ChatMessage(ChatRole.User, "请修改后端"),
                new ChatMessage(ChatRole.Assistant, "修改完成")
            ],
            projectId,
            "context-1",
            "突出说明验证结果。",
            TestContext.Current.CancellationToken);

        Assert.Equal(ChatRole.System, result.Role);
        Assert.Equal(Constants.DefaultAgentAuthor, result.AuthorName);
        Assert.Equal("result", result.AdditionalProperties!["type"]);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Contents));
        Assert.Equal("## 完成\n\n- 已支持 **Markdown**。", text.Text);

        Assert.Equal(modelProviderId, Assert.Single(clientFactory.RequestedIds));
        Assert.Equal(2, client.Messages.Count);
        Assert.Equal(ChatRole.System, client.Messages[0].Role);
        Assert.Contains("突出说明验证结果。", client.Messages[0].Text);
        Assert.Contains("Use Markdown when it improves readability", client.Messages[0].Text);
        Assert.Contains("Plain text is also acceptable", client.Messages[0].Text);
        Assert.DoesNotContain("as plain text", client.Messages[0].Text);
        Assert.Equal(ChatRole.User, client.Messages[1].Role);
        Assert.Contains("请修改后端", client.Messages[1].Text);
        Assert.Contains("修改完成", client.Messages[1].Text);
        Assert.Null(client.Options?.Tools);

        var persisted = Assert.Single(writer.Entries);
        Assert.Equal(projectId, persisted.ProjectId);
        Assert.Equal("context-1", persisted.ContextId);
        Assert.Same(result, Assert.Single(persisted.Messages));

        var usage = Assert.Single(usageRecorder.Entries);
        Assert.Equal(projectId, usage.ProjectId);
        Assert.Equal("context-1", usage.ContextId);
        Assert.Equal("$summary", usage.AgentName);
        Assert.Equal(11, usage.Usage.InputTokenCount);
        Assert.Equal(7, usage.Usage.OutputTokenCount);
        Assert.Equal(18, usage.Usage.TotalTokenCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task CreateResultAsync_EmptySummary_ReturnsFailureResult(string? summaryText)
    {
        var response = summaryText == null
            ? new ChatResponse([])
            : new ChatResponse([new ChatMessage(ChatRole.Assistant, summaryText)]);
        var writer = new RecordingConversationHistoryWriter();
        var service = CreateService(new RecordingChatClient(response), writer);

        var result = await service.CreateResultAsync(
            Guid.NewGuid(),
            [new ChatMessage(ChatRole.User, "input")],
            Guid.NewGuid(),
            "context-1",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("Summary generation failed.", Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text);
        Assert.Same(result, Assert.Single(Assert.Single(writer.Entries).Messages));
    }

    [Fact]
    public async Task CreateResultAsync_ModelFailure_ReturnsFailureResult()
    {
        var writer = new RecordingConversationHistoryWriter();
        var service = CreateService(new RecordingChatClient(new InvalidOperationException("provider failed")), writer);

        var result = await service.CreateResultAsync(
            Guid.NewGuid(),
            [new ChatMessage(ChatRole.User, "input")],
            Guid.NewGuid(),
            "context-1",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("Summary generation failed.", Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text);
        Assert.Same(result, Assert.Single(Assert.Single(writer.Entries).Messages));
    }

    [Fact]
    public async Task CreateResultAsync_Canceled_PropagatesCancellationWithoutPersisting()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();
        var writer = new RecordingConversationHistoryWriter();
        var service = CreateService(
            new RecordingChatClient(new OperationCanceledException(cancellationTokenSource.Token)),
            writer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateResultAsync(
            Guid.NewGuid(),
            [new ChatMessage(ChatRole.User, "input")],
            Guid.NewGuid(),
            "context-1",
            null,
            cancellationTokenSource.Token));

        Assert.Empty(writer.Entries);
    }

    private static AgentTurnSummaryService CreateService(
        IChatClient client,
        RecordingConversationHistoryWriter writer) =>
        new(
            new StubSummaryChatClientFactory(client),
            writer,
            new RecordingUsageRecorder(),
            NullLogger<AgentTurnSummaryService>.Instance);

    private sealed class StubSummaryChatClientFactory(IChatClient client) : ISummaryChatClientFactory
    {
        public List<Guid> RequestedIds { get; } = [];

        public Task<IChatClient?> CreateAsync(Guid modelProviderId, CancellationToken cancellationToken = default)
        {
            RequestedIds.Add(modelProviderId);
            return Task.FromResult<IChatClient?>(client);
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ChatResponse? _response;
        private readonly Exception? _exception;

        public RecordingChatClient(ChatResponse response)
        {
            _response = response;
        }

        public RecordingChatClient(Exception exception)
        {
            _exception = exception;
        }

        public List<ChatMessage> Messages { get; } = [];

        public ChatOptions? Options { get; private set; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Messages.AddRange(messages);
            Options = options;
            return _exception == null
                ? Task.FromResult(_response!)
                : Task.FromException<ChatResponse>(_exception);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingConversationHistoryWriter : IConversationHistoryWriter
    {
        public List<Entry> Entries { get; } = [];

        public Task AppendAsync(
            Guid projectId,
            string contextId,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new Entry(projectId, contextId, messages));
            return Task.CompletedTask;
        }

        public sealed record Entry(Guid ProjectId, string ContextId, IReadOnlyList<ChatMessage> Messages);
    }

    private sealed class RecordingUsageRecorder : IAgentUsageRecorder
    {
        public List<Entry> Entries { get; } = [];

        public Task AddAsync(
            Guid projectId,
            string contextId,
            string agentName,
            ProjectContextUsage usage,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new Entry(projectId, contextId, agentName, usage));
            return Task.CompletedTask;
        }

        public sealed record Entry(
            Guid ProjectId,
            string ContextId,
            string AgentName,
            ProjectContextUsage Usage);
    }
}
