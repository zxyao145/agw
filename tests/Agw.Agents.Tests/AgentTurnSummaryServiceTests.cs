using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Summaries;
using Agw.Shared;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentTurnSummaryServiceTests
{
    [Theory]
    [InlineData(ResultFormat.Markdown, "markdown")]
    [InlineData(ResultFormat.Json, "json")]
    public void CreateResultMessage_TypedFormat_StoresLowercaseString(ResultFormat format, string expected)
    {
        var message = AgentTurnSummaryService.CreateResultMessage("body", format);

        Assert.Equal(expected, Assert.IsType<string>(message.AdditionalProperties!["resultFormat"]));
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(format));
        Assert.Equal(format, JsonSerializer.Deserialize<ResultFormat>($"\"{expected}\""));
    }

    [Theory]
    [InlineData("  {\"value\":\"```<tag>\"}\n ", "{\"value\":\"```<tag>\"}")]
    [InlineData("```json\n{\"approved\":false}\n```\n\n小结：请修改代码。", "{\"approved\":false}")]
    [InlineData("结果如下：\n~~~json\n[{\"score\":38},null]\n~~~\n完成。", "[{\"score\":38},null]")]
    [InlineData("{\"id\":9007199254740993,\"score\":1.00}\n小结：完成。", "{\"id\":9007199254740993,\"score\":1.00}")]
    [InlineData(
        "[1, {\"text\":\"braces {} [] and \\\"quotes\\\"\"}]",
        "[1, {\"text\":\"braces {} [] and \\\"quotes\\\"\"}]"
    )]
    [InlineData("[]", "[]")]
    [InlineData("{}", "{}")]
    public async Task CreateStructuredResultAsync_ValidContainer_PersistsOnlyJsonWithoutModelUsage(
        string finalText,
        string expectedJson
    )
    {
        // Arrange
        var projectId = Guid.CreateVersion7();
        var client = new RecordingChatClient(new ChatResponse([]));
        var writer = new RecordingConversationHistoryWriter();
        var usageRecorder = new RecordingUsageRecorder();
        var clientFactory = new StubSummaryChatClientFactory(client);
        var service = new AgentTurnSummaryService(
            clientFactory,
            writer,
            usageRecorder,
            NullLogger<AgentTurnSummaryService>.Instance
        );

        // Act
        var result = await service.CreateStructuredResultAsync(
            finalText,
            projectId,
            "context-1",
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(ChatRole.Assistant, result.Role);
        Assert.Equal(Constants.DefaultAgentAuthor, result.AuthorName);
        Assert.Equal("result", result.AdditionalProperties!["type"]);
        Assert.Equal("json", result.AdditionalProperties["resultFormat"]);
        Assert.Equal(expectedJson, Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text);
        using var document = JsonDocument.Parse(result.Text);
        Assert.True(document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array);
        Assert.Empty(clientFactory.RequestedIds);
        Assert.Empty(client.Messages);
        Assert.Empty(usageRecorder.Entries);
        var persisted = Assert.Single(writer.Entries);
        Assert.Equal(projectId, persisted.ProjectId);
        Assert.Equal("context-1", persisted.ContextId);
        Assert.Same(result, Assert.Single(persisted.Messages));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Only a summary.")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("\"{}\"")]
    [InlineData("```json\ntrue\n```")]
    [InlineData("```json\n\"{}\"\n```")]
    [InlineData("```json\n{}")]
    [InlineData("{\"x\":1}}")]
    [InlineData("[1]]")]
    [InlineData("```json\n{\"value\":}\n```")]
    [InlineData("{\"nested\":{}, invalid}")]
    [InlineData("[{\"x\":1}")]
    [InlineData("{\"x\":1,}")]
    [InlineData("{}\n[]")]
    [InlineData("```json\n{}\n```\n```json\n[]\n```")]
    public async Task CreateStructuredResultAsync_InvalidOrAmbiguousOutput_FailsWithoutPersisting(string finalText)
    {
        // Arrange
        var writer = new RecordingConversationHistoryWriter();
        var service = CreateService(new RecordingChatClient(new ChatResponse([])), writer);

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.CreateStructuredResultAsync(
                finalText,
                Guid.CreateVersion7(),
                "context-1",
                TestContext.Current.CancellationToken
            )
        );

        // Assert
        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
        Assert.Empty(writer.Entries);
    }

    [Fact]
    public async Task CreateStructuredResultAsync_Canceled_DoesNotPersistResult()
    {
        // Arrange
        var writer = new RecordingConversationHistoryWriter();
        var service = CreateService(new RecordingChatClient(new ChatResponse([])), writer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreateStructuredResultAsync("{}", Guid.CreateVersion7(), "context-1", cancellation.Token)
        );

        // Assert
        Assert.Empty(writer.Entries);
    }

    [Fact]
    public async Task CreateResultAsync_Success_ReturnsAndPersistsTextResultWithUsage()
    {
        var projectId = Guid.CreateVersion7();
        var modelProviderId = Guid.CreateVersion7();
        var client = new RecordingChatClient(
            new ChatResponse([new ChatMessage(ChatRole.Assistant, "  ## 完成\n\n- 已支持 **Markdown**。  ")])
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 11,
                    OutputTokenCount = 7,
                    TotalTokenCount = 18,
                },
            }
        );
        var writer = new RecordingConversationHistoryWriter();
        var usageRecorder = new RecordingUsageRecorder();
        var clientFactory = new StubSummaryChatClientFactory(client);
        var service = new AgentTurnSummaryService(
            clientFactory,
            writer,
            usageRecorder,
            NullLogger<AgentTurnSummaryService>.Instance
        );

        var result = await service.CreateResultAsync(
            modelProviderId,
            [new ChatMessage(ChatRole.User, "请修改后端"), new ChatMessage(ChatRole.Assistant, "修改完成")],
            projectId,
            "context-1",
            "突出说明验证结果。",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ChatRole.Assistant, result.Role);
        Assert.Equal(Constants.DefaultAgentAuthor, result.AuthorName);
        Assert.Equal("result", result.AdditionalProperties!["type"]);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Contents));
        Assert.Equal("markdown", result.AdditionalProperties["resultFormat"]);
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
        var response =
            summaryText == null
                ? new ChatResponse([])
                : new ChatResponse([new ChatMessage(ChatRole.Assistant, summaryText)]);
        var writer = new RecordingConversationHistoryWriter();
        var service = CreateService(new RecordingChatClient(response), writer);

        var result = await service.CreateResultAsync(
            Guid.CreateVersion7(),
            [new ChatMessage(ChatRole.User, "input")],
            Guid.CreateVersion7(),
            "context-1",
            null,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("Summary generation failed.", Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text);
        Assert.Same(result, Assert.Single(Assert.Single(writer.Entries).Messages));
    }

    [Fact]
    public async Task CreateResultAsync_ModelFailure_ReturnsFailureResult()
    {
        var writer = new RecordingConversationHistoryWriter();
        var service = CreateService(new RecordingChatClient(new InvalidOperationException("provider failed")), writer);

        var result = await service.CreateResultAsync(
            Guid.CreateVersion7(),
            [new ChatMessage(ChatRole.User, "input")],
            Guid.CreateVersion7(),
            "context-1",
            null,
            TestContext.Current.CancellationToken
        );

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
            writer
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreateResultAsync(
                Guid.CreateVersion7(),
                [new ChatMessage(ChatRole.User, "input")],
                Guid.CreateVersion7(),
                "context-1",
                null,
                cancellationTokenSource.Token
            )
        );

        Assert.Empty(writer.Entries);
    }

    private static AgentTurnSummaryService CreateService(
        IChatClient client,
        RecordingConversationHistoryWriter writer
    ) =>
        new(
            new StubSummaryChatClientFactory(client),
            writer,
            new RecordingUsageRecorder(),
            NullLogger<AgentTurnSummaryService>.Instance
        );

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

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Messages.AddRange(messages);
            Options = options;
            return _exception == null ? Task.FromResult(_response!) : Task.FromException<ChatResponse>(_exception);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
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
            CancellationToken cancellationToken = default
        )
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
            CancellationToken cancellationToken = default
        )
        {
            Entries.Add(new Entry(projectId, contextId, agentName, usage));
            return Task.CompletedTask;
        }

        public sealed record Entry(Guid ProjectId, string ContextId, string AgentName, ProjectContextUsage Usage);
    }
}
