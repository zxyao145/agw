using Agw.Shared;
using Agw.Shared.Tasks.Entities;
using Agw.Tasks.Domain.Services;

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
                TaskId = Guid.NewGuid(),
                ConversationSequence = 2,
                CreateTime = new DateTime(2024, 1, 1, 3, 0, 0, DateTimeKind.Utc),
                UpdateTime = new DateTime(2024, 1, 1, 3, 0, 1, DateTimeKind.Utc),
            },
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
                ConversationSequence = null,
                CreateTime = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            },
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
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
            TaskId = Guid.NewGuid(),
            ConversationSequence = 1,
            CreateTime = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
        };
        var second = new TaskRecord
        {
            Id = Guid.NewGuid(),
            TaskId = Guid.NewGuid(),
            ConversationSequence = 2,
            CreateTime = new DateTime(2024, 1, 1, 2, 0, 0, DateTimeKind.Utc),
        };

        var result = _service.GetLatest([second, first]);

        Assert.Same(second, result);
    }

    [Fact]
    public void GroupByTaskId_GroupsOrderedRecordsPerTaskId()
    {
        var taskIdA = Guid.NewGuid();
        var taskIdB = Guid.NewGuid();

        var earlier = new TaskRecord
        {
            Id = Guid.NewGuid(),
            TaskId = taskIdA,
            ConversationSequence = 1,
            CreateTime = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
        };
        var later = new TaskRecord
        {
            Id = Guid.NewGuid(),
            TaskId = taskIdA,
            ConversationSequence = 2,
            CreateTime = new DateTime(2024, 1, 1, 2, 0, 0, DateTimeKind.Utc),
        };
        var other = new TaskRecord
        {
            Id = Guid.NewGuid(),
            TaskId = taskIdB,
            ConversationSequence = 1,
            CreateTime = new DateTime(2024, 1, 1, 3, 0, 0, DateTimeKind.Utc),
        };

        var groups = _service.GroupByTaskId([later, other, earlier]);

        Assert.Equal([earlier.Id, later.Id], groups[taskIdA.Normalize()].Select(record => record.Id));
        Assert.Equal([other.Id], groups[taskIdB.Normalize()].Select(record => record.Id));
    }

    [Fact]
    public void FindTask_ReturnsDirectMatchByTaskId()
    {
        var task = new ProjectTask { Id = Guid.NewGuid(), ContextId = "context-1" };

        var result = _service.FindTask(task.Id.Normalize(), [task], []);

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
    public void FindTask_WhenDirectMatchMissing_UsesLatestRecordSession()
    {
        var olderTask = new ProjectTask { Id = Guid.NewGuid(), ContextId = "context-a" };
        var newerTask = new ProjectTask { Id = Guid.NewGuid(), ContextId = "context-b" };
        var records = new[]
        {
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                TaskId = olderTask.Id,
                CreateTime = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
                UpdateTime = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            },
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                TaskId = newerTask.Id,
                CreateTime = new DateTime(2024, 1, 1, 2, 0, 0, DateTimeKind.Utc),
                UpdateTime = new DateTime(2024, 1, 1, 3, 0, 0, DateTimeKind.Utc),
            },
        };

        var result = _service.FindTask("unknown-session", [olderTask, newerTask], records);

        Assert.Same(newerTask, result);
    }

    [Fact]
    public void FindTask_BlankTaskId_ReturnsNull()
    {
        var task = new ProjectTask { Id = Guid.NewGuid(), ContextId = "context-1" };

        var result = _service.FindTask(" ", [task], []);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldDeleteTask_ProjectBackedTask_ReturnsFalse()
    {
        var task = new ProjectTask { ProjectId = Guid.NewGuid() };

        var result = _service.ShouldDeleteTask(task);

        Assert.False(result);
    }
}
