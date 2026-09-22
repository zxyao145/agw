using System.Text.Json;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Summaries;
using Agw.Projects.Application;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Agents.AI;
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
    public void Stamp_StreamAdvances_PreservesFirstTimestampThroughAggregationAndJson()
    {
        // Arrange
        var clock = new TestClock(CreatedAt.AddMinutes(1));
        var timestamps = new ResponseMessageTimestamps(clock);
        var first = new ChatResponseUpdate(ChatRole.Assistant, "first ") { MessageId = "item", CreatedAt = CreatedAt };
        var second = new ChatResponseUpdate(ChatRole.Assistant, "second") { MessageId = "item" };

        // Act
        timestamps.Stamp(first);
        clock.Now = CreatedAt.AddHours(1);
        timestamps.Stamp(second);
        var message = new[] { first, second }.ToChatResponse().Messages.Single();
        var restored = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message))!;

        // Assert
        Assert.Equal(CreatedAt, second.CreatedAt);
        Assert.Equal(CreatedAt, restored.ToAiMessage()!.CreatedAt);
        Assert.Equal(CreatedAt, MessageTimestampMetadata.GetCreatedAt(restored.AdditionalProperties));
        Assert.Equal("first second", restored.Text);
    }

    [Fact]
    public void Stamp_ExternalResultAndNextRun_UsesIndependentTimestamps()
    {
        // Arrange
        var clock = new TestClock(CreatedAt);
        var first = new AgentResponseUpdate(ChatRole.Assistant, "answer") { MessageId = "item" };
        var second = new AgentResponseUpdate(ChatRole.Assistant, "next answer") { MessageId = "item" };

        // Act
        new ResponseMessageTimestamps(clock).Stamp(first);
        clock.Now = CreatedAt.AddHours(1);
        new ResponseMessageTimestamps(clock).Stamp(second);

        // Assert
        Assert.Equal(CreatedAt, first.ToAiMessage()!.CreatedAt);
        Assert.Equal(clock.Now, second.ToAiMessage()!.CreatedAt);
    }

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
