using System.Text.Json;
using Agw.Agents.Execution.Agents.History;
using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiAgentSdk;
using PiAgentSdk.MAF;

namespace Agw.Agents.Tests;

public sealed class HistoryRecordingRegressionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task CalibrateAsync_UnidentifiedMessages_PersistsEveryMessage(string? messageId)
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new PiMessageAdapter());
        var messages = new[] { "first", "second", "first" }
            .Select(text => new ChatMessage(ChatRole.Assistant, text) { MessageId = messageId })
            .ToArray();

        await recording.CalibrateAsync(messages, null, TestContext.Current.CancellationToken);
        await recording.CalibrateAsync(messages, null, TestContext.Current.CancellationToken);
        await recording.FinishAsync(completed: true, TestContext.Current.CancellationToken);

        var stored = await fixture.ReadMessagesAsync();
        Assert.Equal(["first", "second", "first"], stored.Select(message => message.Text));
        Assert.Equal(3, stored.Select(message => message.MessageId).Distinct().Count());
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate)]
    [InlineData(ConversationHistoryWriteMode.Interval)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd)]
    public async Task CalibrateAsync_ModelStreamWithoutNativeId_PreservesOneRowAcrossFlushes(
        ConversationHistoryWriteMode mode
    )
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(mode);
        var recording = fixture.CreateRecording(new ModelMessageAdapter());
        await using var turn = fixture.BeginTurn();
        var token = TestContext.Current.CancellationToken;
        string? messageId = null;

        foreach (var part in new[] { "The", " ", "user", " ", "user" })
        {
            await recording.RecordAsync(new AgentResponseUpdate(ChatRole.Assistant, part), token);
            await turn.Buffer.FlushAsync(token);
            var message = Assert.Single(await fixture.ReadMessagesAsync());
            messageId ??= message.MessageId;
            Assert.Equal(messageId, message.MessageId);
        }
        await recording.CalibrateAsync(recording.Drain().ToAgentResponse().Messages.ToList(), null, token);
        await recording.FinishAsync(completed: true, token);
        await turn.Buffer.FlushAsync(token);

        var stored = Assert.Single(await fixture.ReadMessagesAsync());
        Assert.Equal(messageId, stored.MessageId);
        Assert.Equal("The user user", stored.Text);
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(stored));
        Assert.Empty(recording.Drain());
    }

    [Fact]
    public async Task CalibrateAsync_BatchRepeatingOneIdentity_StoresEveryMessage()
    {
        // 批准恢复后的同一次历史通知会重复出现同一个 callId 与同一个消息 ID。
        // The notification after an approval repeats one call id and one message id.
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new PiMessageAdapter());
        var token = TestContext.Current.CancellationToken;

        await recording.CalibrateAsync(
            [
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "approval requested")]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "executed output")]),
                new ChatMessage(ChatRole.Assistant, "first") { MessageId = "shared" },
                new ChatMessage(ChatRole.Assistant, "second") { MessageId = "shared" },
            ],
            null,
            token
        );
        await recording.FinishAsync(completed: true, token);

        var stored = await fixture.ReadMessagesAsync();
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
    }

    [Fact]
    public async Task CalibrateAsync_ClaudeFragmentsAroundNotification_PreservesAllContent()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new ClaudeMessageAdapter());
        var processor = CreateClaudeProcessor();
        await StartClaudeMessageAsync(processor, recording, "message", "The ");
        await ProcessClaudeAsync(
            processor,
            recording,
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
            recording,
            Event(
                new
                {
                    type = "content_block_delta",
                    index = 0,
                    delta = new { type = "text_delta", text = "user" },
                }
            )
        );

        await ApplyClaudeHistoryAsync(recording, processor.GetType().GetMethod("CompleteRun")!.Invoke(processor, null));
        await recording.FinishAsync(completed: true, TestContext.Current.CancellationToken);

        var stored = await fixture.ReadMessagesAsync();
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
    public async Task RecordAsync_PiHistoryCallbackAndTurnEnd_PreservesOneMessage(string text, string? error)
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new PiMessageAdapter());
        var type = typeof(PiAgentAIAgent).Assembly.GetType(
            "PiAgentSdk.MAF.Internal.PiEventMapper",
            throwOnError: true
        )!;
        var mapper = Activator.CreateInstance(type, [null])!;
        var toUpdate = type.GetMethod("ToUpdate")!.CreateDelegate<Func<PiEvent, AgentResponseUpdate?>>(mapper);
        var toHistory = type.GetMethod("ToHistoryMessages")!
            .CreateDelegate<Func<PiTurnEndEvent, IReadOnlyList<ChatMessage>>>(mapper);
        var assistant = new PiAssistantMessage
        {
            Content = text.Length == 0 ? [] : [new PiTextContent { Text = text }],
            StopReason = error == null ? "stop" : "error",
            ErrorMessage = error,
        };
        toUpdate(new PiMessageEvent("message_start") { Message = assistant });
        if (text.Length > 0)
        {
            var delta = toUpdate(
                new PiMessageUpdateEvent
                {
                    AssistantMessageEvent = new PiTextDelta { ContentIndex = 0, Delta = text },
                }
            );
            Assert.NotNull(delta);
            await recording.RecordAsync(delta, TestContext.Current.CancellationToken);
        }
        if (toUpdate(new PiMessageEvent("message_end") { Message = assistant }) is { } ended)
            await recording.RecordAsync(ended, TestContext.Current.CancellationToken);
        var turnEnd = new PiTurnEndEvent { Message = assistant };

        await recording.CalibrateAsync(toHistory(turnEnd), null, TestContext.Current.CancellationToken);
        Assert.Null(toUpdate(turnEnd));
        await recording.FinishAsync(completed: true, TestContext.Current.CancellationToken);

        var stored = await fixture.ReadMessagesAsync();
        Assert.Equal(text, string.Concat(stored.Select(message => message.Text)));
        Assert.All(
            stored.Where(message => message.Role == ChatRole.Assistant),
            message => Assert.Equal(error != null, ConversationHistoryMetadata.IsModelHistoryExcluded(message))
        );
        Assert.Equal(
            error,
            stored.SelectMany(message => message.Contents).OfType<ErrorContent>().SingleOrDefault()?.Message
        );
        var live = recording.Drain().ToAgentResponse().Messages;
        Assert.Equal(text, string.Concat(live.Select(message => message.Text)));
        Assert.Equal(
            error,
            live.SelectMany(message => message.Contents).OfType<ErrorContent>().SingleOrDefault()?.Message
        );
    }

    [Fact]
    public async Task RecordAsync_ClaudeHistoryBeforeSupplement_PreservesTextAndToolInOneMessage()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new ClaudeMessageAdapter());
        var processor = CreateClaudeProcessor();
        await StartClaudeMessageAsync(processor, recording, "message", "Checking ");

        await ProcessClaudeAsync(processor, recording, Event(new { type = "message_stop" }));
        await ProcessClaudeAsync(
            processor,
            recording,
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
        await recording.FinishAsync(completed: true, TestContext.Current.CancellationToken);

        var stored = Assert.Single(await fixture.ReadMessagesAsync());
        Assert.Collection(
            stored.Contents,
            content => Assert.Equal("Checking ", Assert.IsType<TextContent>(content).Text),
            content => Assert.Equal("call", Assert.IsType<FunctionCallContent>(content).CallId)
        );
        var live = Assert.Single(recording.Drain().ToAgentResponse().Messages);
        Assert.Equal(stored.MessageId, live.MessageId);
        Assert.Equal(stored.Text, live.Text);
        Assert.Single(live.Contents.OfType<FunctionCallContent>());
    }

    [Fact]
    public async Task FinishAsync_ClaudePartialCleanupCallback_PreservesModelHistory()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new ClaudeMessageAdapter());
        var processor = CreateClaudeProcessor();
        await StartClaudeMessageAsync(processor, recording, "message", "partial");

        var batch = processor.GetType().GetMethod("CompleteRun")!.Invoke(processor, null);
        await ApplyClaudeHistoryAsync(recording, batch);
        await recording.FinishAsync(completed: true, TestContext.Current.CancellationToken);

        var stored = Assert.Single(await fixture.ReadMessagesAsync());
        Assert.Equal("partial", stored.Text);
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(stored));
        Assert.Equal("partial", recording.Drain().ToAgentResponse().Text);
    }

    [Fact]
    public async Task FinishAsync_ClaudeCompletedMessageThenInterruption_PreservesBothMessages()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new ClaudeMessageAdapter());
        var processor = CreateClaudeProcessor();
        await StartClaudeMessageAsync(processor, recording, "first", "completed");
        await ProcessClaudeAsync(
            processor,
            recording,
            Event(new { type = "message_delta", delta = new { stop_reason = "end_turn" } })
        );
        await ProcessClaudeAsync(processor, recording, Event(new { type = "message_stop" }));
        await ProcessClaudeAsync(
            processor,
            recording,
            new AssistantMessage
            {
                Id = "first-uuid",
                ApiMessageId = "first",
                SessionId = "session",
                Model = "model",
                Content = [new TextBlock { Text = "completed" }],
            }
        );
        await StartClaudeMessageAsync(processor, recording, "second", "partial");

        await ApplyClaudeHistoryAsync(recording, processor.GetType().GetMethod("CompleteRun")!.Invoke(processor, null));
        await recording.FinishAsync(completed: true, TestContext.Current.CancellationToken);

        var stored = await fixture.ReadMessagesAsync();
        Assert.Equal(["completed", "partial"], stored.Select(message => message.Text));
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(stored[0]));
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(stored[1]));
    }

    [Fact]
    public async Task RecordAsync_InterleavedProducers_PersistsFirstVisibleOrder()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var turn = fixture.BeginTurn();
        var first = fixture.CreateRecording(new CodexMessageAdapter());
        var second = fixture.CreateRecording(new CodexMessageAdapter());
        var token = TestContext.Current.CancellationToken;

        await first.RecordAsync(new AgentResponseUpdate(ChatRole.Assistant, "a1") { MessageId = "a1" }, token);
        await second.RecordAsync(new AgentResponseUpdate(ChatRole.Assistant, "b1") { MessageId = "b1" }, token);
        await first.RecordAsync(new AgentResponseUpdate(ChatRole.Assistant, "a2") { MessageId = "a2" }, token);
        await first.FinishAsync(completed: true, token);
        await second.FinishAsync(completed: true, token);
        await turn.Buffer.FlushAsync(token);

        Assert.Equal(["a1", "b1", "a2"], (await fixture.ReadMessagesAsync()).Select(message => message.Text));
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate)]
    [InlineData(ConversationHistoryWriteMode.Interval)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd)]
    public async Task CalibrateAsync_StreamedResponse_PersistsOnceAndEmitsOnlyDeltas(ConversationHistoryWriteMode mode)
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(mode);
        await using var turn = fixture.BeginTurn();
        var recording = fixture.CreateRecording(new ClaudeMessageAdapter());
        var token = TestContext.Current.CancellationToken;
        string? id = null;
        var live = new List<AgentResponseUpdate>();
        foreach (var part in new[] { "The", " ", "user", " wants", " a review" })
        {
            await recording.RecordAsync(
                new AgentResponseUpdate(ChatRole.Assistant, part) { MessageId = "answer" },
                token
            );
            live.AddRange(recording.Drain());
            await turn.Buffer.FlushAsync(token);
            var row = Assert.Single(await fixture.ReadMessagesAsync());
            id ??= row.MessageId;
            Assert.Equal(id, row.MessageId);
        }

        await recording.CalibrateAsync(
            [new ChatMessage(ChatRole.Assistant, "The user wants a review") { MessageId = "answer" }],
            null,
            token
        );
        await recording.FinishAsync(completed: true, token);
        await turn.Buffer.FlushAsync(token);

        Assert.Empty(recording.Drain());
        Assert.Equal(["The", " ", "user", " wants", " a review"], live.Select(update => update.Text));
        var stored = Assert.Single(await fixture.ReadMessagesAsync());
        Assert.Equal(id, stored.MessageId);
        Assert.Equal(live.ToAgentResponse().Text, stored.Text);
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(stored));
    }

    [Fact]
    public async Task RecordAsync_CompleteMessageBetweenDeltas_PersistsOneMessage()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new ClaudeMessageAdapter());
        var token = TestContext.Current.CancellationToken;
        await recording.RecordAsync(
            new AgentResponseUpdate(ChatRole.Assistant, "Checking ") { MessageId = "answer" },
            token
        );
        var first = Assert.Single(recording.Drain());

        await recording.CalibrateAsync(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    [new TextContent("Checking "), new FunctionCallContent("call", "lookup")]
                )
                {
                    MessageId = "answer",
                },
            ],
            null,
            token
        );
        Assert.Empty(recording.Drain());
        await recording.RecordAsync(
            new AgentResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call", "lookup")])
            {
                MessageId = "answer",
            },
            token
        );
        await recording.FinishAsync(completed: true, token);

        var tool = Assert.Single(recording.Drain());
        Assert.Equal(first.MessageId, tool.MessageId);
        Assert.IsType<FunctionCallContent>(Assert.Single(tool.Contents));
        var stored = Assert.Single(await fixture.ReadMessagesAsync());
        Assert.Equal("Checking ", stored.Text);
        Assert.Single(stored.Contents.OfType<FunctionCallContent>());
    }

    [Fact]
    public async Task FinishAsync_LaterDelta_PreservesMessageIdentityAndModelHistory()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new ModelMessageAdapter());
        var token = TestContext.Current.CancellationToken;
        await recording.RecordAsync(
            new AgentResponseUpdate(ChatRole.Assistant, "partial") { MessageId = "answer" },
            token
        );
        var first = Assert.Single(recording.Drain());
        await recording.FinishAsync(completed: true, token);
        Assert.Empty(recording.Drain());

        await recording.RecordAsync(
            new AgentResponseUpdate(ChatRole.Assistant, " tail") { MessageId = "answer" },
            token
        );

        Assert.Equal(first.MessageId, Assert.Single(recording.Drain()).MessageId);
        var stored = Assert.Single(await fixture.ReadMessagesAsync());
        Assert.Equal("partial tail", stored.Text);
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(stored));
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate)]
    [InlineData(ConversationHistoryWriteMode.Interval)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd)]
    public async Task RecordAsync_ReusedSourceId_StaysIsolatedAcrossProducers(ConversationHistoryWriteMode mode)
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(mode);
        var token = TestContext.Current.CancellationToken;
        for (var index = 0; index < 2; index++)
        {
            await using var turn = fixture.BeginTurn();
            var recording = fixture.CreateRecording(new CodexMessageAdapter());
            foreach (var text in new[] { "a", "a", " ", "b" })
            {
                await recording.RecordAsync(
                    new AgentResponseUpdate(ChatRole.Assistant, text) { MessageId = "item_0" },
                    token
                );
                await turn.Buffer.FlushAsync(token);
            }
            await recording.FinishAsync(completed: true, token);
            await turn.Buffer.FlushAsync(token);
        }

        var rows = await fixture.ReadMessagesAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, message => Assert.Equal("aa b", message.Text));
        Assert.NotEqual(rows[0].MessageId, rows[1].MessageId);
    }

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
        HistoryRecording recording,
        string id,
        string text
    )
    {
        await ProcessClaudeAsync(
            processor,
            recording,
            Event(new { type = "message_start", message = new { id, model = "model" } })
        );
        await ProcessClaudeAsync(
            processor,
            recording,
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
            recording,
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

    /// <summary>
    /// 与 SDK 相同：先交出已完成的历史批次，再输出本条消息的增量。
    /// Same as the SDK: the completed history batch is handed over before this message's deltas are emitted.
    /// </summary>
    private static async Task ProcessClaudeAsync(object processor, HistoryRecording recording, IMessage message)
    {
        var result = processor.GetType().GetMethod("Process")!.Invoke(processor, [message])!;
        await ApplyClaudeHistoryAsync(
            recording,
            result.GetType().GetProperty("CompletedHistoryBatch")!.GetValue(result)
        );
        var updates = (IReadOnlyList<AgentResponseUpdate>)result.GetType().GetProperty("Updates")!.GetValue(result)!;
        foreach (var update in updates)
            await recording.RecordAsync(update, TestContext.Current.CancellationToken);
    }

    private static async Task ApplyClaudeHistoryAsync(HistoryRecording recording, object? batch)
    {
        if (batch == null)
            return;
        var updates =
            (IReadOnlyList<AgentResponseUpdate>)batch.GetType().GetProperty("ResponseUpdates")!.GetValue(batch)!;
        await recording.CalibrateAsync(
            updates.ToAgentResponse().Messages.ToList(),
            null,
            TestContext.Current.CancellationToken
        );
    }
}
