using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using Agw.Agents.Execution.Agents.History;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.History;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Projects;
using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgentSdk;
using PiAgentSdk.MAF;

namespace Agw.Agents.Tests;

public sealed class NormalizedHistoryRegressionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task CompleteAsync_UnidentifiedMessages_PersistsEveryMessage(string? messageId)
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync();
        var capture = fixture.CreateCapture(new ModelMessageAdapter());
        var messages = new[] { "first", "second", "first" }
            .Select(text => new ChatMessage(ChatRole.Assistant, text) { MessageId = messageId })
            .ToArray();

        await capture.CompleteAsync(messages, TestContext.Current.CancellationToken);
        await capture.FinishAsync(ConversationMessageState.Completed, TestContext.Current.CancellationToken);

        var stored = await fixture.ReadAsync();
        Assert.Equal(["first", "second", "first"], stored.Select(message => message.Text));
        Assert.Equal(3, stored.Select(message => message.MessageId).Distinct().Count());
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate)]
    [InlineData(ConversationHistoryWriteMode.Interval)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd)]
    public async Task CompleteAsync_ModelStreamWithoutNativeId_PreservesOneRowAcrossFlushes(
        ConversationHistoryWriteMode mode
    )
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync(mode);
        var capture = fixture.CreateCapture(new ModelMessageAdapter());
        await using var scope = fixture.BeginPersistenceScope();
        var token = TestContext.Current.CancellationToken;
        string? messageId = null;

        foreach (var part in new[] { "The", " ", "user", " ", "user" })
        {
            await capture.ProcessAsync(new AgentResponseUpdate(ChatRole.Assistant, part), token);
            await ConversationHistoryPersistenceContext.FlushAsync(token);
            var message = Assert.Single(await fixture.ReadAsync());
            messageId ??= message.MessageId;
            Assert.Equal(messageId, message.MessageId);
        }
        await capture.CompleteAsync(capture.Drain().ToAgentResponse().Messages.ToList(), token);
        await capture.FinishAsync(ConversationMessageState.Completed, token);
        await ConversationHistoryPersistenceContext.FlushAsync(token);

        var stored = Assert.Single(await fixture.ReadAsync());
        Assert.Equal(messageId, stored.MessageId);
        Assert.Equal("The user user", stored.Text);
        Assert.Equal("completed", stored.AdditionalProperties!["messageState"]!.ToString());
    }

    [Fact]
    public async Task CompleteAsync_SnapshotsRepeatingOneIdentity_StoresEveryMessage()
    {
        // 批准恢复后的同一次历史通知会重复出现同一个 callId 与同一个消息 ID。
        // The notification after an approval repeats one call id and one message id.
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync();
        var capture = fixture.CreateCapture(new ModelMessageAdapter());
        var token = TestContext.Current.CancellationToken;

        await capture.CompleteAsync(
            [
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "approval requested")]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "executed output")]),
                new ChatMessage(ChatRole.Assistant, "first") { MessageId = "shared" },
                new ChatMessage(ChatRole.Assistant, "second") { MessageId = "shared" },
            ],
            token
        );
        await capture.FinishAsync(ConversationMessageState.Completed, token);

        var stored = await fixture.ReadAsync();
        Assert.Equal(4, stored.Select(message => message.MessageId).Distinct().Count());
        Assert.Equal(
            ["approval requested", "executed output"],
            stored
                .Where(message => message.Role == ChatRole.Tool)
                .Select(message => message.Contents.OfType<FunctionResultContent>().Single().Result?.ToString())
        );
        Assert.Equal(
            ["first", "second"],
            stored.Where(message => message.Role == ChatRole.Assistant).Select(message => message.Text)
        );
        Assert.All(
            stored,
            message => Assert.Equal("completed", message.AdditionalProperties!["messageState"]!.ToString())
        );
    }

    [Fact]
    public async Task CompleteAsync_ClaudeFragmentsAroundNotification_PreservesAllContent()
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync();
        var capture = fixture.CreateCapture(new ClaudeMessageAdapter());
        var processor = CreateClaudeProcessor();
        await StartClaudeMessageAsync(processor, capture, "message", "The ");
        await ProcessClaudeAsync(
            processor,
            capture,
            new SystemMessage
            {
                Id = "retry",
                Subtype = "api_retry",
                SessionId = "session",
                Data = new Dictionary<string, object> { ["error"] = "retry notification" },
            }
        );
        await ProcessClaudeAsync(
            processor,
            capture,
            Event(
                new
                {
                    type = "content_block_delta",
                    index = 0,
                    delta = new { type = "text_delta", text = "user" },
                }
            )
        );

        await ApplyClaudeHistoryAsync(capture, processor.GetType().GetMethod("CompleteRun")!.Invoke(processor, null));
        await capture.FinishAsync(ConversationMessageState.Interrupted, TestContext.Current.CancellationToken);

        var stored = await fixture.ReadAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal("The user", Assert.Single(stored, message => message.Role == ChatRole.Assistant).Text);
        Assert.Single(
            Assert.Single(stored, message => message.Role == ChatRole.System).Contents.OfType<ErrorContent>()
        );
    }

    [Theory]
    [InlineData("complete answer", null)]
    [InlineData("partial", "model failed")]
    [InlineData("", "model failed")]
    public async Task ProcessAsync_PiSnapshotAfterHistoryCallback_HandlesDuplicateAndPreservesOneMessage(
        string text,
        string? error
    )
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync();
        var capture = fixture.CreateCapture(new PiMessageAdapter());
        var type = typeof(PiAgentAIAgent).Assembly.GetType(
            "PiAgentSdk.MAF.Internal.PiEventMapper",
            throwOnError: true
        )!;
        var mapper = Activator.CreateInstance(type, [null, true])!;
        var toUpdate = type.GetMethod("ToUpdate")!.CreateDelegate<Func<PiEvent, AgentResponseUpdate?>>(mapper);
        var toHistory = type.GetMethod("ToHistoryMessages")!
            .CreateDelegate<Func<PiTurnEndEvent, IReadOnlyList<ChatMessage>>>(mapper);
        var assistant = new PiAssistantMessage
        {
            Content = text.Length == 0 ? [] : [new PiTextContent { Text = text }],
            ErrorMessage = error,
        };
        toUpdate(new PiMessageEvent("message_start") { Message = assistant });
        if (text.Length > 0)
        {
            var delta = toUpdate(
                new PiMessageUpdateEvent
                {
                    AssistantMessageEvent = new PiTextDelta
                    {
                        ContentIndex = 0,
                        Delta = text[..Math.Min(4, text.Length)],
                    },
                }
            );
            Assert.NotNull(delta);
            await capture.ProcessAsync(delta, TestContext.Current.CancellationToken);
        }
        if (toUpdate(new PiMessageEvent("message_end") { Message = assistant }) is { } ended)
            await capture.ProcessAsync(ended, TestContext.Current.CancellationToken);
        var turnEnd = new PiTurnEndEvent { Message = assistant };

        await capture.CompleteAsync(toHistory(turnEnd), TestContext.Current.CancellationToken);
        var snapshot = toUpdate(turnEnd);
        Assert.NotNull(snapshot);
        var handled = await capture.ProcessAsync(snapshot, TestContext.Current.CancellationToken);
        await capture.FinishAsync(ConversationMessageState.Completed, TestContext.Current.CancellationToken);

        Assert.True(handled);
        var stored = Assert.Single(await fixture.ReadAsync(), message => message.Role == ChatRole.Assistant);
        Assert.Equal(text, stored.Text);
        Assert.Equal(error == null ? "completed" : "failed", stored.AdditionalProperties!["messageState"]!.ToString());
        Assert.Equal(error, stored.Contents.OfType<ErrorContent>().SingleOrDefault()?.Message);
        var live = Assert.Single(
            NormalizedResponseAggregation.Aggregate(capture.Drain()).Messages,
            message => message.Role == ChatRole.Assistant
        );
        Assert.Equal(text, live.Text);
        Assert.Equal(error, live.Contents.OfType<ErrorContent>().SingleOrDefault()?.Message);
    }

    [Fact]
    public async Task ProcessAsync_ClaudeHistoryBeforeSupplement_PreservesTextAndToolInOneMessage()
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync();
        var capture = fixture.CreateCapture(new ClaudeMessageAdapter());
        var processor = CreateClaudeProcessor();
        await StartClaudeMessageAsync(processor, capture, "message", "Checking ");

        await ProcessClaudeAsync(processor, capture, Event(new { type = "message_stop" }));
        await ProcessClaudeAsync(
            processor,
            capture,
            new AssistantMessage
            {
                Id = "assistant-uuid",
                ApiMessageId = "message",
                SessionId = "session",
                Model = "model",
                Content =
                [
                    new TextBlock { Text = "Checking " },
                    new ToolUseBlock
                    {
                        Id = "call",
                        Name = "Bash",
                        Input = new Dictionary<string, object>(),
                    },
                ],
            }
        );
        await capture.FinishAsync(ConversationMessageState.Completed, TestContext.Current.CancellationToken);

        var stored = Assert.Single(await fixture.ReadAsync());
        Assert.Collection(
            stored.Contents,
            content => Assert.Equal("Checking ", Assert.IsType<TextContent>(content).Text),
            content => Assert.Equal("call", Assert.IsType<FunctionCallContent>(content).CallId)
        );
        Assert.Equal("completed", stored.AdditionalProperties!["messageState"]!.ToString());
        var live = Assert.Single(NormalizedResponseAggregation.Aggregate(capture.Drain()).Messages);
        Assert.Equal(stored.MessageId, live.MessageId);
        Assert.Equal(stored.Text, live.Text);
        Assert.Single(live.Contents.OfType<FunctionCallContent>());
    }

    [Theory]
    [InlineData(ConversationMessageState.Interrupted)]
    [InlineData(ConversationMessageState.Failed)]
    public async Task FinishAsync_ClaudePartialCleanupCallback_PreservesIncompleteState(ConversationMessageState state)
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync();
        var capture = fixture.CreateCapture(new ClaudeMessageAdapter());
        var processor = CreateClaudeProcessor();
        await StartClaudeMessageAsync(processor, capture, "message", "partial");

        var batch = processor.GetType().GetMethod("CompleteRun")!.Invoke(processor, null);
        await ApplyClaudeHistoryAsync(capture, batch);
        await capture.FinishAsync(state, TestContext.Current.CancellationToken);

        var stored = Assert.Single(await fixture.ReadAsync());
        Assert.Equal("partial", stored.Text);
        Assert.Equal(state.ToString().ToLowerInvariant(), stored.AdditionalProperties!["messageState"]!.ToString());
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(stored));
    }

    [Fact]
    public async Task FinishAsync_ClaudeCompletedMessageThenInterruption_PreservesBothStates()
    {
        using var owner = EnterOwner();
        await using var fixture = await HistoryDatabase.CreateAsync();
        var capture = fixture.CreateCapture(new ClaudeMessageAdapter());
        var processor = CreateClaudeProcessor();
        await StartClaudeMessageAsync(processor, capture, "first", "completed");
        await ProcessClaudeAsync(
            processor,
            capture,
            Event(new { type = "message_delta", delta = new { stop_reason = "end_turn" } })
        );
        await ProcessClaudeAsync(processor, capture, Event(new { type = "message_stop" }));
        await ProcessClaudeAsync(
            processor,
            capture,
            new AssistantMessage
            {
                Id = "first-uuid",
                ApiMessageId = "first",
                SessionId = "session",
                Model = "model",
                Content = [new TextBlock { Text = "completed" }],
            }
        );
        await StartClaudeMessageAsync(processor, capture, "second", "partial");

        await ApplyClaudeHistoryAsync(capture, processor.GetType().GetMethod("CompleteRun")!.Invoke(processor, null));
        await capture.FinishAsync(ConversationMessageState.Interrupted, TestContext.Current.CancellationToken);

        var stored = await fixture.ReadAsync();
        Assert.Equal(["completed", "partial"], stored.Select(message => message.Text));
        Assert.Equal(
            ["completed", "interrupted"],
            stored.Select(message => message.AdditionalProperties!["messageState"]!.ToString())
        );
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(stored[0]));
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(stored[1]));
    }

    private static IDisposable EnterOwner() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );

    // 直接执行当前 SDK 的消息处理器，保留 SDK 的回调顺序。
    // Execute the installed SDK processor with its callback ordering.
    private static object CreateClaudeProcessor() =>
        Activator.CreateInstance(
            typeof(ClaudeCodeAIAgent).Assembly.GetType(
                "ClaudeCodeSdk.MAF.ClaudeStreamingMessageProcessor",
                throwOnError: true
            )!,
            [Array.Empty<ChatMessage>(), true, true]
        )!;

    private static StreamEvent Event(object value) =>
        new() { SessionId = "session", Event = JsonSerializer.SerializeToElement(value) };

    private static async Task StartClaudeMessageAsync(
        object processor,
        NormalizedChatHistoryProvider.Capture capture,
        string id,
        string text
    )
    {
        await ProcessClaudeAsync(
            processor,
            capture,
            Event(new { type = "message_start", message = new { id, model = "model" } })
        );
        await ProcessClaudeAsync(
            processor,
            capture,
            Event(
                new
                {
                    type = "content_block_start",
                    index = 0,
                    content_block = new { type = "text", text = "" },
                }
            )
        );
        await ProcessClaudeAsync(
            processor,
            capture,
            Event(
                new
                {
                    type = "content_block_delta",
                    index = 0,
                    delta = new { type = "text_delta", text },
                }
            )
        );
    }

    private static async Task ProcessClaudeAsync(
        object processor,
        NormalizedChatHistoryProvider.Capture capture,
        IMessage message
    )
    {
        var result = processor.GetType().GetMethod("Process")!.Invoke(processor, [message])!;
        await ApplyClaudeHistoryAsync(capture, result.GetType().GetProperty("CompletedHistoryBatch")!.GetValue(result));
        var updates = (IReadOnlyList<AgentResponseUpdate>)result.GetType().GetProperty("Updates")!.GetValue(result)!;
        foreach (var update in updates)
            await capture.ProcessAsync(update, TestContext.Current.CancellationToken);
    }

    private static ValueTask ApplyClaudeHistoryAsync(NormalizedChatHistoryProvider.Capture capture, object? batch)
    {
        if (batch == null)
            return ValueTask.CompletedTask;
        var updates =
            (IReadOnlyList<AgentResponseUpdate>)batch.GetType().GetProperty("ResponseUpdates")!.GetValue(batch)!;
        return capture.CompleteAsync(
            ClaudeCodeChatHistoryProvider.PrepareResponseMessages(updates.ToAgentResponse().Messages),
            TestContext.Current.CancellationToken
        );
    }

    private sealed class HistoryDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly ServiceProvider _services;
        private readonly EfCoreChatHistoryProvider _provider;
        private readonly Guid _projectId = Guid.NewGuid();

        private HistoryDatabase(ConversationHistoryWriteMode mode)
        {
            _options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(_connection).Options;
            _services = new ServiceCollection()
                .AddScoped<IProjectsDbContext>(_ => new AgwDbContext(_options))
                .BuildServiceProvider();
            _provider = new EfCoreChatHistoryProvider(
                _services.GetRequiredService<IServiceScopeFactory>(),
                Agw.Shared.Coordination.InMemoryApplicationLock.Shared,
                NullLogger<EfCoreChatHistoryProvider>.Instance,
                TimeProvider.System,
                options: Microsoft.Extensions.Options.Options.Create(new ConversationHistoryOptions { Mode = mode })
            );
        }

        public static async Task<HistoryDatabase> CreateAsync(
            ConversationHistoryWriteMode mode = ConversationHistoryWriteMode.Immediate
        )
        {
            var fixture = new HistoryDatabase(mode);
            await fixture._connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var db = new AgwDbContext(fixture._options);
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.Projects.Add(
                new Project
                {
                    Id = fixture._projectId,
                    Name = "History",
                    CreateBy = "tester",
                }
            );
            db.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = Guid.NewGuid(),
                    ProjectId = fixture._projectId,
                    ContextId = "regression",
                    Title = "History",
                    CreateBy = "tester",
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return fixture;
        }

        public NormalizedChatHistoryProvider.Capture CreateCapture(IAgentMessageAdapter<AgentResponseUpdate> adapter) =>
            new(
                new ConversationMessageWriteScope
                {
                    ProjectId = _projectId,
                    ContextId = "regression",
                    Generation = 0,
                    ProducerId = Guid.NewGuid(),
                },
                _provider,
                adapter,
                TimeProvider.System,
                true,
                "test"
            );

        public IAsyncDisposable BeginPersistenceScope() =>
            ConversationHistoryPersistenceContext.BeginScope(_provider, _projectId, "regression", 0);

        public async Task<List<ChatMessage>> ReadAsync()
        {
            await using var db = new AgwDbContext(_options);
            var rows = await db
                .ProjectConversationChatHistories.OrderBy(row => row.ConversationSequence)
                .ToListAsync(TestContext.Current.CancellationToken);
            return rows.Select(row => row.ToChatMessage()!).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
