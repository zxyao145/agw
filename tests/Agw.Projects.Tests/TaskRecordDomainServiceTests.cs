using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Extensions;
using Agw.Projects.Domain.Services;

namespace Agw.Projects.Tests;

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
                CreateTime = new DateTimeOffset(2024, 1, 1, 3, 0, 0, TimeSpan.Zero),
                UpdateTime = new DateTimeOffset(2024, 1, 1, 3, 0, 1, TimeSpan.Zero),
            },
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
                ConversationSequence = null,
                CreateTime = new DateTimeOffset(2024, 1, 1, 1, 0, 0, TimeSpan.Zero),
            },
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
                ConversationSequence = 2,
                CreateTime = new DateTimeOffset(2024, 1, 1, 2, 0, 0, TimeSpan.Zero),
                UpdateTime = new DateTimeOffset(2024, 1, 1, 2, 0, 1, TimeSpan.Zero),
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
            CreateTime = new DateTimeOffset(2024, 1, 1, 1, 0, 0, TimeSpan.Zero),
        };
        var second = new TaskRecord
        {
            Id = Guid.NewGuid(),
            TaskId = Guid.NewGuid(),
            ConversationSequence = 2,
            CreateTime = new DateTimeOffset(2024, 1, 1, 2, 0, 0, TimeSpan.Zero),
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
            CreateTime = new DateTimeOffset(2024, 1, 1, 1, 0, 0, TimeSpan.Zero),
        };
        var later = new TaskRecord
        {
            Id = Guid.NewGuid(),
            TaskId = taskIdA,
            ConversationSequence = 2,
            CreateTime = new DateTimeOffset(2024, 1, 1, 2, 0, 0, TimeSpan.Zero),
        };
        var other = new TaskRecord
        {
            Id = Guid.NewGuid(),
            TaskId = taskIdB,
            ConversationSequence = 1,
            CreateTime = new DateTimeOffset(2024, 1, 1, 3, 0, 0, TimeSpan.Zero),
        };

        var groups = _service.GroupByTaskId([later, other, earlier]);

        Assert.Equal([earlier.Id, later.Id], groups[taskIdA.Normalize()].Select(record => record.Id));
        Assert.Equal([other.Id], groups[taskIdB.Normalize()].Select(record => record.Id));
    }

    [Fact]
    public void FindTask_ReturnsDirectMatchByTaskId()
    {
        var task = new TaskProjection { TaskId = Guid.NewGuid(), ContextId = "context-1" };

        var result = _service.FindTask(task.TaskId.Normalize(), [task], []);

        Assert.Same(task, result);
    }

    [Fact]
    public void FindTask_ReturnsDirectMatchByNormalizedTaskId()
    {
        var task = new TaskProjection { TaskId = Guid.NewGuid(), ContextId = "context-1" };

        var result = _service.FindTask(task.TaskId.Normalize().ToUpperInvariant(), [task], []);

        Assert.Same(task, result);
    }

    [Fact]
    public void FindTask_WhenDirectMatchMissing_UsesLatestRecordSession()
    {
        var olderTask = new TaskProjection { TaskId = Guid.NewGuid(), ContextId = "context-a" };
        var newerTask = new TaskProjection { TaskId = Guid.NewGuid(), ContextId = "context-b" };
        var records = new[]
        {
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                TaskId = olderTask.TaskId,
                CreateTime = new DateTimeOffset(2024, 1, 1, 1, 0, 0, TimeSpan.Zero),
                UpdateTime = new DateTimeOffset(2024, 1, 1, 1, 0, 0, TimeSpan.Zero),
            },
            new TaskRecord
            {
                Id = Guid.NewGuid(),
                TaskId = newerTask.TaskId,
                CreateTime = new DateTimeOffset(2024, 1, 1, 2, 0, 0, TimeSpan.Zero),
                UpdateTime = new DateTimeOffset(2024, 1, 1, 3, 0, 0, TimeSpan.Zero),
            },
        };

        var result = _service.FindTask("unknown-session", [olderTask, newerTask], records);

        Assert.Same(newerTask, result);
    }

    [Fact]
    public void FindTask_BlankTaskId_ReturnsNull()
    {
        var task = new TaskProjection { TaskId = Guid.NewGuid(), ContextId = "context-1" };

        var result = _service.FindTask(" ", [task], []);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldDeleteTask_ProjectBackedTask_ReturnsFalse()
    {
        var task = new TaskProjection { ProjectId = Guid.NewGuid() };

        var result = _service.ShouldDeleteTask(task);

        Assert.False(result);
    }
}
