using Agw.Projects.Domain.Services;
using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Tests;

public class ConversationHistoryDomainServiceTests
{
    private static readonly DateTimeOffset FinishedAt = new(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private readonly ConversationHistoryDomainService _service = new();

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

        var ordered = _service.Order(records);

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

        var result = _service.GetLatest([second, first]);

        Assert.Same(second, result);
    }

    [Fact]
    public void GetLatest_EmptyHistory_ReturnsNull()
    {
        Assert.Null(_service.GetLatest([]));
    }

    [Fact]
    public void TryFinishTask_RunningTaskFails_FinishesEveryRecordWithError()
    {
        var records = CreateTaskRecords(TaskExecutionStatus.Running);

        var finished = _service.TryFinishTask(
            records,
            TaskExecutionStatus.Running,
            TaskExecutionStatus.Failed,
            "Execution failed",
            FinishedAt
        );

        Assert.True(finished);
        Assert.All(
            records,
            record =>
            {
                Assert.Equal(TaskExecutionStatus.Failed, record.Status);
                Assert.Equal("Execution failed", record.TaskErrorMessage);
                Assert.Equal(FinishedAt, record.FinishedTime);
                Assert.Equal(FinishedAt, record.UpdateTime);
            }
        );
    }

    [Fact]
    public void TryFinishTask_RunningTaskSucceeds_ClearsTaskError()
    {
        var records = CreateTaskRecords(TaskExecutionStatus.Running);
        foreach (var record in records)
        {
            record.TaskErrorMessage = "Earlier failure";
        }

        var finished = _service.TryFinishTask(
            records,
            TaskExecutionStatus.Running,
            TaskExecutionStatus.Succeeded,
            "Ignored",
            FinishedAt
        );

        Assert.True(finished);
        Assert.All(
            records,
            record =>
            {
                Assert.Equal(TaskExecutionStatus.Succeeded, record.Status);
                Assert.Null(record.TaskErrorMessage);
            }
        );
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Pending)]
    [InlineData(TaskExecutionStatus.Succeeded)]
    [InlineData(TaskExecutionStatus.Failed)]
    public void TryFinishTask_TaskNotRunning_LeavesRecordsUnchanged(TaskExecutionStatus currentStatus)
    {
        var records = CreateTaskRecords(currentStatus);

        var finished = _service.TryFinishTask(
            records,
            currentStatus,
            TaskExecutionStatus.Failed,
            "Execution failed",
            FinishedAt
        );

        Assert.False(finished);
        Assert.All(
            records,
            record =>
            {
                Assert.Equal(currentStatus, record.Status);
                Assert.Null(record.FinishedTime);
            }
        );
    }

    private static ProjectConversationChatHistory[] CreateTaskRecords(TaskExecutionStatus status)
    {
        var taskId = Guid.CreateVersion7();
        return
        [
            new ProjectConversationChatHistory
            {
                Id = Guid.CreateVersion7(),
                TaskId = taskId,
                Status = status,
                CreateTime = new DateTimeOffset(2024, 1, 1, 1, 0, 0, TimeSpan.Zero),
            },
            new ProjectConversationChatHistory
            {
                Id = Guid.CreateVersion7(),
                TaskId = taskId,
                Status = status,
                CreateTime = new DateTimeOffset(2024, 1, 1, 2, 0, 0, TimeSpan.Zero),
            },
        ];
    }
}
