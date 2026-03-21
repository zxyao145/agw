using Agw.Domain.Services;
using Agw.Shared;
using Agw.Shared.Tasks.Entities;

namespace Agw.Tasks.Tests;

public class TaskRecordDomainServiceTests
{
    private readonly TaskRecordDomainService _service = new();

    [Fact]
    public void Order_SortsBySequenceThenTimestamps()
    {
        var records = new[]
        {
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                ContextId = "context-a",
                ConversationSequence = 2,
                CreateTime = new DateTime(2024, 1, 1, 3, 0, 0, DateTimeKind.Utc),
                UpdateTime = new DateTime(2024, 1, 1, 3, 0, 1, DateTimeKind.Utc),
            },
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                ContextId = "context-a",
                ConversationSequence = null,
                CreateTime = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            },
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                ContextId = "context-a",
                ConversationSequence = 2,
                CreateTime = new DateTime(2024, 1, 1, 2, 0, 0, DateTimeKind.Utc),
                UpdateTime = new DateTime(2024, 1, 1, 2, 0, 1, DateTimeKind.Utc),
            },
        };

        var ordered = _service.Order(records);

        Assert.Equal([records[1].Id, records[2].Id, records[0].Id], ordered.Select(record => record.Id));
    }

    [Fact]
    public void GetLatest_ReturnsLastOrderedRecord()
    {
        var first = new TaskRecord
        {
            Id = Guid.NewGuid(),
            ContextId = "context-a",
            ConversationSequence = 1,
            CreateTime = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
        };
        var second = new TaskRecord
        {
            Id = Guid.NewGuid(),
            ContextId = "context-a",
            ConversationSequence = 2,
            CreateTime = new DateTime(2024, 1, 1, 2, 0, 0, DateTimeKind.Utc),
        };

        var result = _service.GetLatest([second, first]);

        Assert.Same(second, result);
    }

    [Fact]
    public void GroupByContext_GroupsOrderedRecordsPerContext()
    {
        var earlier = new TaskRecord
        {
            Id = Guid.NewGuid(),
            ContextId = "context-a",
            ConversationSequence = 1,
            CreateTime = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
        };
        var later = new TaskRecord
        {
            Id = Guid.NewGuid(),
            ContextId = "context-a",
            ConversationSequence = 2,
            CreateTime = new DateTime(2024, 1, 1, 2, 0, 0, DateTimeKind.Utc),
        };
        var other = new TaskRecord
        {
            Id = Guid.NewGuid(),
            ContextId = "context-b",
            ConversationSequence = 1,
            CreateTime = new DateTime(2024, 1, 1, 3, 0, 0, DateTimeKind.Utc),
        };

        var groups = _service.GroupByContext([later, other, earlier]);

        Assert.Equal([earlier.Id, later.Id], groups["context-a"].Select(record => record.Id));
        Assert.Equal([other.Id], groups["context-b"].Select(record => record.Id));
    }

    [Fact]
    public void FindTask_ReturnsDirectMatchByContextId()
    {
        var task = new ProjectTask { Id = Guid.NewGuid(), ContextId = "session-1" };

        var result = _service.FindTask("session-1", [task], []);

        Assert.Same(task, result);
    }

    [Fact]
    public void FindTask_ReturnsDirectMatchByNormalizedTaskId()
    {
        var task = new ProjectTask { Id = Guid.NewGuid(), ContextId = "context-1" };

        var result = _service.FindTask(task.Id.Normalize().ToUpperInvariant(), [task], []);

        Assert.Same(task, result);
    }

    [Fact]
    public void FindTask_WhenDirectMatchMissing_UsesLatestRecordContext()
    {
        var olderTask = new ProjectTask { Id = Guid.NewGuid(), ContextId = "context-a" };
        var newerTask = new ProjectTask { Id = Guid.NewGuid(), ContextId = "context-b" };
        var records = new[]
        {
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                ContextId = olderTask.ContextId,
                CreateTime = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
                UpdateTime = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            },
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                ContextId = newerTask.ContextId,
                CreateTime = new DateTime(2024, 1, 1, 2, 0, 0, DateTimeKind.Utc),
                UpdateTime = new DateTime(2024, 1, 1, 3, 0, 0, DateTimeKind.Utc),
            },
        };

        var result = _service.FindTask("unknown-session", [olderTask, newerTask], records);

        Assert.Same(newerTask, result);
    }

    [Fact]
    public void FindTask_BlankSessionId_ReturnsNull()
    {
        var task = new ProjectTask { Id = Guid.NewGuid(), ContextId = "context-1" };

        var result = _service.FindTask(" ", [task], []);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("not-a-guid", true)]
    [InlineData("d2719d59-e4d6-4ddb-9be3-e6fe0f79d56c", false)]
    public void ShouldDeleteTask_ReturnsExpectedValue(string projectId, bool expected)
    {
        var task = new ProjectTask { ProjectId = projectId };

        var result = _service.ShouldDeleteTask(task);

        Assert.Equal(expected, result);
    }
}
