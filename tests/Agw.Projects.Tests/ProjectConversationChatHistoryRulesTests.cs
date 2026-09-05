using Agw.Projects.Domain.Rules;
using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Tests;

public class ProjectConversationChatHistoryRulesTests
{
    [Fact]
    public void Order_SortsBySequenceThenTimestamps()
    {
        var records = new[]
        {
            new ProjectConversationChatHistory
            {
                Id = Guid.CreateVersion7(),
                TaskId = Guid.CreateVersion7(),
                ConversationSequence = 2,
                CreateTime = new DateTimeOffset(2024, 1, 1, 3, 0, 0, TimeSpan.Zero),
                UpdateTime = new DateTimeOffset(2024, 1, 1, 3, 0, 1, TimeSpan.Zero),
            },
            new ProjectConversationChatHistory
            {
                Id = Guid.CreateVersion7(),
                TaskId = Guid.CreateVersion7(),
                ConversationSequence = null,
                CreateTime = new DateTimeOffset(2024, 1, 1, 1, 0, 0, TimeSpan.Zero),
            },
            new ProjectConversationChatHistory
            {
                Id = Guid.CreateVersion7(),
                TaskId = Guid.CreateVersion7(),
                ConversationSequence = 2,
                CreateTime = new DateTimeOffset(2024, 1, 1, 2, 0, 0, TimeSpan.Zero),
                UpdateTime = new DateTimeOffset(2024, 1, 1, 2, 0, 1, TimeSpan.Zero),
            },
        };

        var ordered = ProjectConversationChatHistoryRules.Order(records);

        Assert.Equal([records[1].Id, records[2].Id, records[0].Id], ordered.Select(record => record.Id));
    }

    [Fact]
    public void GetLatest_ReturnsLastOrderedRecord()
    {
        var first = new ProjectConversationChatHistory
        {
            Id = Guid.CreateVersion7(),
            TaskId = Guid.CreateVersion7(),
            ConversationSequence = 1,
            CreateTime = new DateTimeOffset(2024, 1, 1, 1, 0, 0, TimeSpan.Zero),
        };
        var second = new ProjectConversationChatHistory
        {
            Id = Guid.CreateVersion7(),
            TaskId = Guid.CreateVersion7(),
            ConversationSequence = 2,
            CreateTime = new DateTimeOffset(2024, 1, 1, 2, 0, 0, TimeSpan.Zero),
        };

        var result = ProjectConversationChatHistoryRules.GetLatest([second, first]);

        Assert.Same(second, result);
    }

    [Fact]
    public void GetLatest_EmptyHistory_ReturnsNull()
    {
        Assert.Null(ProjectConversationChatHistoryRules.GetLatest([]));
    }
}
