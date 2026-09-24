using System.Text.Json;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Summaries;
using Agw.Projects.Application;
using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class MessageTimestampTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 20, 13, 38, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateUserChatMessage_WithOptionalTimestamp_PreservesOrUsesClock(bool supplied)
    {
        // Arrange
        var clock = new TestClock(CreatedAt.AddMinutes(1));
        var input = new AgwUserInput
        {
            CreatedAt = supplied ? CreatedAt : null,
            Contents = [new AgwTextContent { Content = "question" }],
        };

        // Act
        var message = AgwMessageUtil.CreateUserChatMessage(input, clock).ToAiMessage();

        // Assert
        Assert.Equal(supplied ? CreatedAt : clock.GetUtcNow(), message!.CreatedAt);
    }

    [Fact]
    public void Append_StreamAdvances_PreservesFirstTimestampThroughAggregationAndJson()
    {
        // Arrange
        var clock = new TestClock(CreatedAt.AddMinutes(1));
        var projection = new AgentMessageProjection(CreateWriteScope(), clock);

        // Act
        var first = projection.Append(
            new ChatMessage(ChatRole.Assistant, "first ") { MessageId = "item", CreatedAt = CreatedAt }
        );
        clock.Now = CreatedAt.AddHours(1);
        var second = projection.Append(new ChatMessage(ChatRole.Assistant, "second") { MessageId = "item" });
        var message = Assert.Single(projection.ReadMessages());
        var restored = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message))!;

        // Assert
        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Equal(CreatedAt, second.CreatedAt);
        Assert.Equal(CreatedAt, restored.ToAiMessage()!.CreatedAt);
        Assert.Equal(CreatedAt, MessageTimestampMetadata.GetCreatedAt(restored.AdditionalProperties));
        Assert.Equal("first second", restored.Text);
    }

    [Fact]
    public void Append_NextRun_UsesIndependentTimestamps()
    {
        // Arrange
        var clock = new TestClock(CreatedAt);

        // Act
        var first = new AgentMessageProjection(CreateWriteScope(), clock).Append(
            new ChatMessage(ChatRole.Assistant, "answer") { MessageId = "item" }
        );
        clock.Now = CreatedAt.AddHours(1);
        var second = new AgentMessageProjection(CreateWriteScope(), clock).Append(
            new ChatMessage(ChatRole.Assistant, "next answer") { MessageId = "item" }
        );

        // Assert
        Assert.Equal(CreatedAt, first.ToAiMessage()!.CreatedAt);
        Assert.Equal(clock.Now, second.ToAiMessage()!.CreatedAt);
        Assert.NotEqual(first.MessageId, second.MessageId);
    }

    private static ConversationMessageWriteScope CreateWriteScope() =>
        new()
        {
            ProjectId = Guid.CreateVersion7(),
            ContextId = "context",
            Generation = 0,
            ProducerId = Guid.CreateVersion7(),
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToAiMessages_HistoryRecord_UsesMessageTimestampOrRecordCreation(bool supplied)
    {
        // Arrange
        var message = new ChatMessage(ChatRole.Assistant, "answer");
        if (supplied)
            MessageTimestampMetadata.EnsureCreatedAt(message, CreatedAt);
        var record = new ProjectConversationChatHistory
        {
            CreateTime = CreatedAt.AddMinutes(2),
            ConversationPayload = JsonSerializer.Serialize(
                message,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            ),
        };

        // Act
        var result = TaskExecutionMapper.ToAiMessages(record).Single();

        // Assert
        Assert.Equal(supplied ? CreatedAt : record.CreateTime, result.CreatedAt);
    }

    [Fact]
    public void CreateResultMessage_WithTimestamp_RoundTripsInOutputContract()
    {
        // Arrange
        var result = AgentTurnSummaryService.CreateResultMessage("done", createdAt: CreatedAt);

        // Act
        var json = JsonSerializer.Serialize(result.ToAiMessage());
        var restored = JsonSerializer.Deserialize<AgwMessage>(json)!;

        // Assert
        Assert.Equal(CreatedAt, restored.CreatedAt);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public TestClock(DateTimeOffset now)
        {
            Now = now;
        }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
