using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Projects.Application.History;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Tests;

public partial class EfCoreChatHistoryProviderTests
{
    [Fact]
    public async Task StreamingResponse_OnlyTailChanges_ReusesPrefixAndRetrySnapshots()
    {
        // Arrange
        var converter = new CountingChatMessageConverter();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(converter);
        await using var fixture = await HistoryBatchFixture.CreateAsync(
            ConversationHistoryWriteMode.TurnEnd,
            jsonSerializerOptions: options
        );
        await using var scope = fixture.BeginScope();
        var session = new FakeAgentSession();
        fixture.Provider.InitializeSessionState(session, "buffered", fixture.ProjectId);
        var token = TestContext.Current.CancellationToken;
        var stream = await fixture.Provider.BeginStreamingResponseAsync(new FakeAgent(), session, [], token);
        Assert.NotNull(stream);
        await stream.AppendAsync(new(ChatRole.Assistant, "first") { MessageId = "first" }, token);
        await stream.AppendAsync(new(ChatRole.Assistant, "second") { MessageId = "second" }, token);
        await ConversationHistoryPersistenceContext.FlushAsync(token);
        var original = await fixture.ReadRowsAsync();

        // Act: reads and an uncertain commit must reuse the same changed snapshot.
        await stream.AppendAsync(new(ChatRole.Assistant, " continued") { MessageId = "second" }, token);
        Assert.Empty(await fixture.ReadModelTextsAsync());
        Assert.Empty(await fixture.ReadModelTextsAsync());
        fixture.Commits.ThrowAfterCommit = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => ConversationHistoryPersistenceContext.FlushAsync(token));
        await ConversationHistoryPersistenceContext.FlushAsync(token);

        // Assert
        Assert.Equal(1, converter.Writes["first"]);
        Assert.Equal(2, converter.Writes["second"]);
        var updated = await fixture.ReadRowsAsync();
        Assert.Equal(original.Select(row => row.Id), updated.Select(row => row.Id));
        Assert.Equal(["first", "second continued"], updated.Select(row => row.GetText()));

        await stream.CompleteAsync(
            [
                new(ChatRole.Assistant, "first") { MessageId = "first" },
                new(ChatRole.Assistant, "second continued") { MessageId = "second" },
            ],
            token
        );
        Assert.Equal(["first", "second continued"], await fixture.ReadModelTextsAsync());
        await ConversationHistoryPersistenceContext.FlushAsync(token);
        var completed = await fixture.ReadRowsAsync();
        Assert.Equal(original.Select(row => row.Id), completed.Select(row => row.Id));
        Assert.All(
            completed,
            row => Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(row.ToChatMessage()!))
        );
    }

    [Fact]
    public async Task StreamingResponse_UncertainCommitThenCompletion_UpdatesStableRecordAndPendingModelView()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using (fixture.BeginScope())
        {
            var session = new FakeAgentSession();
            fixture.Provider.InitializeSessionState(session, "buffered", fixture.ProjectId, "node:1", "First node");
            var stream = await fixture.Provider.BeginStreamingResponseAsync(
                new FakeAgent(),
                session,
                [new ChatMessage(ChatRole.User, "question")],
                TestContext.Current.CancellationToken
            );
            Assert.NotNull(stream);
            var update = new ChatResponseUpdate(ChatRole.Assistant, "first")
            {
                MessageId = "answer",
                AdditionalProperties = new() { ["modelName"] = "test-model" },
            };
            await stream.AppendAsync(update, TestContext.Current.CancellationToken);
            update.Contents.Clear();
            fixture.Commits.ThrowAfterCommit = true;
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                ConversationHistoryPersistenceContext.FlushAsync(TestContext.Current.CancellationToken)
            );
            var first = await fixture.ReadRowsAsync();
            Assert.Equal(2, first.Count);
            Assert.Equal("first", first[1].GetText());
            Assert.Equal(["question"], await fixture.ReadModelTextsAsync("node:1"));
            await stream.AppendAsync(
                new ChatResponseUpdate(ChatRole.Assistant, " second") { MessageId = "answer" },
                TestContext.Current.CancellationToken
            );
            await ConversationHistoryPersistenceContext.FlushAsync(TestContext.Current.CancellationToken);
            var second = await fixture.ReadRowsAsync();
            Assert.Equal(first[1].Id, second[1].Id);
            Assert.Equal("first second", second[1].GetText());
            await stream.CompleteAsync(
                [
                    new ChatMessage(ChatRole.Assistant, "first second")
                    {
                        MessageId = "answer",
                        AdditionalProperties = new() { ["modelName"] = "test-model" },
                    },
                ],
                TestContext.Current.CancellationToken
            );
            Assert.Equal(["question", "first second"], await fixture.ReadModelTextsAsync("node:1"));
            Assert.Empty(await fixture.ReadModelTextsAsync("node:2"));
        }
        var completed = await fixture.ReadRowsAsync();
        Assert.Equal(2, completed.Count);
        Assert.Equal(completed[0].TaskId, completed[1].TaskId);
        var message = completed[1].ToChatMessage()!;
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(message));
        Assert.Equal("test-model", message.AdditionalProperties!["modelName"]?.ToString());
        Assert.Equal("First node", message.AdditionalProperties!["nodeName"]?.ToString());
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate, 16777216)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, 1)]
    public async Task StreamingResponse_ImmediateOrCapacityTrigger_CommitsAndReplacesSnapshot(
        ConversationHistoryWriteMode mode,
        long capacity
    )
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(mode, capacity);
        await using (fixture.BeginScope())
        {
            var session = new FakeAgentSession();
            fixture.Provider.InitializeSessionState(session, "buffered", fixture.ProjectId);
            var stream = await fixture.Provider.BeginStreamingResponseAsync(
                new FakeAgent(),
                session,
                [],
                TestContext.Current.CancellationToken
            );
            Assert.NotNull(stream);
            await stream.AppendAsync(
                new ChatResponseUpdate(ChatRole.Assistant, "partial"),
                TestContext.Current.CancellationToken
            );
            var first = Assert.Single(await fixture.ReadRowsAsync());
            await stream.CompleteAsync(
                [new ChatMessage(ChatRole.Assistant, "complete")],
                TestContext.Current.CancellationToken
            );
            var final = Assert.Single(await fixture.ReadRowsAsync());
            Assert.Equal(first.Id, final.Id);
            Assert.Equal("complete", final.GetText());
        }
    }

    [Fact]
    public async Task StreamingResponse_ResetAfterSnapshot_RejectsLateFinalUpdate()
    {
        await using var fixture = await HistoryBatchFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var scope = fixture.BeginScope();
        var session = new FakeAgentSession();
        fixture.Provider.InitializeSessionState(session, "buffered", fixture.ProjectId);
        var stream = await fixture.Provider.BeginStreamingResponseAsync(
            new FakeAgent(),
            session,
            [],
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(stream);
        await stream.AppendAsync(
            new ChatResponseUpdate(ChatRole.Assistant, "partial"),
            TestContext.Current.CancellationToken
        );
        await ConversationHistoryPersistenceContext.FlushAsync(TestContext.Current.CancellationToken);
        await using (var db = fixture.CreateContext())
            await db.ProjectConversations.ExecuteUpdateAsync(
                update => update.SetProperty(row => row.Generation, 1),
                TestContext.Current.CancellationToken
            );
        await stream.CompleteAsync(
            [new ChatMessage(ChatRole.Assistant, "late complete")],
            TestContext.Current.CancellationToken
        );
        var failure = await Assert.ThrowsAsync<Agw.Shared.Exceptions.AgwException>(() => scope.DisposeAsync().AsTask());
        Assert.Equal(Agw.Shared.Exceptions.ErrorCodes.ConversationSessionConflict.Code, failure.Code);
        Assert.Equal("partial", Assert.Single(await fixture.ReadRowsAsync()).GetText());
    }

    private sealed class CountingChatMessageConverter : JsonConverter<ChatMessage>
    {
        private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);
        public Dictionary<string, int> Writes { get; } = [];

        public override ChatMessage? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) => JsonSerializer.Deserialize<ChatMessage>(ref reader, _options);

        public override void Write(Utf8JsonWriter writer, ChatMessage value, JsonSerializerOptions options)
        {
            if (value.MessageId is { } id)
                Writes[id] = Writes.GetValueOrDefault(id) + 1;
            JsonSerializer.Serialize(writer, value, _options);
        }
    }
}
