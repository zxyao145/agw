using Agw.Domain.Services;
using Agw.Shared;
using Agw.Shared.Enums;
using Agw.Shared.Tasks.Entities;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Agw.Tasks.Tests;

public class ProjectTaskDomainServiceTests
{
    private readonly ProjectTaskDomainService _service = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TryPrepareForCreate_MissingRequiredFields_ReturnsFalse()
    {
        var task = new ProjectTask
        {
            Description = " ",
            ContextId = "context",
            AgentId = Guid.NewGuid(),
        };
        var initialRecord = new TaskRecord
        {
            TaskId = Guid.NewGuid(),
            ConversationPayload = JsonSerializer.Serialize(new ChatMessage(ChatRole.User, "hello"), JsonOptions),
        };

        var result = _service.TryPrepareForCreate(task, initialRecord, "tester");

        Assert.False(result);
        Assert.Equal(Guid.Empty, task.Id);
        Assert.Equal(Guid.Empty, initialRecord.Id);
    }

    [Fact]
    public void TryPrepareForCreate_ValidTask_InitializesTaskAndInitialRecord()
    {
        var before = DateTime.UtcNow;
        var task = new ProjectTask
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Title = "  New task  ",
            Description = "  Investigate issue  ",
            ContextId = "context-1",
            AgentId = Guid.NewGuid(),
        };
        var initialRecord = new TaskRecord
        {
            TaskId = Guid.NewGuid(),
            ConversationPayload = JsonSerializer.Serialize(new ChatMessage(ChatRole.User, "hello"), JsonOptions),
        };

        var result = _service.TryPrepareForCreate(task, initialRecord, "tester");

        Assert.True(result);
        Assert.NotEqual(Guid.Empty, task.Id);
        Assert.Equal("New task", task.Title);
        Assert.Equal("Investigate issue", task.Description);
        Assert.Equal(ProjectTaskStatus.Pending, task.Status);
        Assert.Equal("tester", task.CreateBy);
        Assert.Equal("tester", task.UpdateBy);
        Assert.InRange(task.CreateTime, before, DateTime.UtcNow);
        Assert.Equal(task.CreateTime, task.UpdateTime);

        Assert.NotEqual(Guid.Empty, initialRecord.Id);
        Assert.Equal(task.Id, initialRecord.TaskId);
        Assert.Equal(Constants.DefaultAuthor, initialRecord.AgentName);
        Assert.Equal(task.CreateTime, initialRecord.CreateTime);
        Assert.Equal(task.CreateTime, initialRecord.UpdateTime);
    }

    [Fact]
    public void TryApplyUpdate_BlankInput_ReturnsFalse()
    {
        var task = new ProjectTask { ContextId = "context", Description = "existing" };
        var latestRecord = new TaskRecord { TaskId = Guid.NewGuid(), AgentName = "agent" };

        var result = _service.TryApplyUpdate(task, latestRecord, "Updated description", "   ", out var record);

        Assert.False(result);
        Assert.Null(record);
    }

    [Fact]
    public void TryApplyUpdate_BlankDescriptionAfterTrim_ReturnsFalse()
    {
        var task = new ProjectTask { ContextId = "context", Description = "existing" };
        var latestRecord = new TaskRecord { TaskId = Guid.NewGuid(), AgentName = "agent" };

        var result = _service.TryApplyUpdate(task, latestRecord, "   ", "input", out var record);

        Assert.False(result);
        Assert.Null(record);
    }

    [Fact]
    public void TryApplyUpdate_ValidInput_CreatesNextConversationRecord()
    {
        var task = new ProjectTask
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ContextId = "context-1",
            Description = "existing",
        };
        var latestRecord = new TaskRecord
        {
            TaskId = Guid.NewGuid(),
            AgentName = "agent-a",
            ConversationSequence = 3,
        };
        var before = DateTime.UtcNow;

        var result = _service.TryApplyUpdate(task, latestRecord, "  Updated description  ", "  user input  ", out var record);

        Assert.True(result);
        Assert.NotNull(record);
        Assert.Equal("Updated description", task.Description);
        Assert.InRange(task.UpdateTime!.Value, before, DateTime.UtcNow);
        Assert.Equal(task.Id.Normalize(), record!.TaskId.Normalize());
        Assert.Equal(latestRecord.AgentName, record.AgentName);
        Assert.Equal(4, record.ConversationSequence);
        Assert.Equal("user input", record.GetText());
        Assert.Equal(ChatRole.User, record.ToChatMessage()!.Role);
    }

    [Fact]
    public void TryUpdateTitle_BlankTitle_ReturnsFalse()
    {
        var task = new ProjectTask { Title = "Original" };

        var result = _service.TryUpdateTitle(task, "   ", "tester");

        Assert.False(result);
        Assert.Equal("Original", task.Title);
    }

    [Fact]
    public void TryUpdateTitle_ValidTitle_TrimsAndSetsMetadata()
    {
        var before = DateTime.UtcNow;
        var task = new ProjectTask { Title = "Original" };

        var result = _service.TryUpdateTitle(task, "  Updated title  ", "tester");

        Assert.True(result);
        Assert.Equal("Updated title", task.Title);
        Assert.Equal("tester", task.UpdateBy);
        Assert.InRange(task.UpdateTime!.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public void TryReorder_NonPendingTask_ReturnsFalse()
    {
        var task = new ProjectTask { Status = ProjectTaskStatus.Running };

        var result = _service.TryReorder(task, DateTime.UtcNow, "tester");

        Assert.False(result);
    }

    [Fact]
    public void TryReorder_PendingTask_UpdatesMetadata()
    {
        var task = new ProjectTask { Status = ProjectTaskStatus.Pending };
        var reorderedAt = DateTime.UtcNow.AddMinutes(5);

        var result = _service.TryReorder(task, reorderedAt, "tester");

        Assert.True(result);
        Assert.Equal("tester", task.UpdateBy);
        Assert.Equal(reorderedAt, task.UpdateTime);
    }

    [Theory]
    [InlineData(ProjectTaskStatus.Pending)]
    [InlineData(ProjectTaskStatus.Running)]
    public void TryCancel_PendingOrRunningTask_CancelsTask(ProjectTaskStatus status)
    {
        var task = new ProjectTask { Status = status };
        var before = DateTime.UtcNow;

        var result = _service.TryCancel(task, "tester");

        Assert.True(result);
        Assert.Equal(ProjectTaskStatus.Canceled, task.Status);
        Assert.Equal("tester", task.UpdateBy);
        Assert.InRange(task.UpdateTime!.Value, before, DateTime.UtcNow);
        Assert.Equal(task.UpdateTime, task.FinishedTime);
    }

    [Fact]
    public void TryCancel_CompletedTask_ReturnsFalse()
    {
        var task = new ProjectTask { Status = ProjectTaskStatus.Succeeded };

        var result = _service.TryCancel(task, "tester");

        Assert.False(result);
        Assert.Equal(ProjectTaskStatus.Succeeded, task.Status);
    }

    [Fact]
    public void TryMarkRunning_PendingTask_Succeeds()
    {
        var before = DateTime.UtcNow;
        var task = new ProjectTask { Status = ProjectTaskStatus.Pending };

        var result = _service.TryMarkRunning(task, "tester");

        Assert.True(result);
        Assert.Equal(ProjectTaskStatus.Running, task.Status);
        Assert.Equal("tester", task.UpdateBy);
        Assert.InRange(task.UpdateTime!.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public void TryMarkRunning_NonPendingTask_ReturnsFalse()
    {
        var task = new ProjectTask { Status = ProjectTaskStatus.Succeeded };

        var result = _service.TryMarkRunning(task, "tester");

        Assert.False(result);
    }

    [Fact]
    public void TryMarkSucceeded_RunningTask_SetsSuccessState()
    {
        var before = DateTime.UtcNow;
        var task = new ProjectTask
        {
            Status = ProjectTaskStatus.Running,
            ErrorMessage = "old error",
        };

        var result = _service.TryMarkSucceeded(task, "tester");

        Assert.True(result);
        Assert.Equal(ProjectTaskStatus.Succeeded, task.Status);
        Assert.Null(task.ErrorMessage);
        Assert.Equal("tester", task.UpdateBy);
        Assert.InRange(task.UpdateTime!.Value, before, DateTime.UtcNow);
        Assert.Equal(task.UpdateTime, task.FinishedTime);
    }

    [Fact]
    public void TryMarkSucceeded_NonRunningTask_ReturnsFalse()
    {
        var task = new ProjectTask { Status = ProjectTaskStatus.Pending };

        var result = _service.TryMarkSucceeded(task, "tester");

        Assert.False(result);
    }

    [Fact]
    public void TryMarkFailed_RunningTask_SetsFailureState()
    {
        var before = DateTime.UtcNow;
        var task = new ProjectTask { Status = ProjectTaskStatus.Running };

        var result = _service.TryMarkFailed(task, "boom", "tester");

        Assert.True(result);
        Assert.Equal(ProjectTaskStatus.Failed, task.Status);
        Assert.Equal("boom", task.ErrorMessage);
        Assert.Equal("tester", task.UpdateBy);
        Assert.InRange(task.UpdateTime!.Value, before, DateTime.UtcNow);
        Assert.Equal(task.UpdateTime, task.FinishedTime);
    }

    [Fact]
    public void TryMarkFailed_NonRunningTask_ReturnsFalse()
    {
        var task = new ProjectTask { Status = ProjectTaskStatus.Pending };

        var result = _service.TryMarkFailed(task, "boom", "tester");

        Assert.False(result);
    }

    [Fact]
    public void GetNextPending_ReturnsOldestByUpdateTimeThenCreateTime()
    {
        var first = new ProjectTask
        {
            Id = Guid.NewGuid(),
            CreateTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdateTime = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        var second = new ProjectTask
        {
            Id = Guid.NewGuid(),
            CreateTime = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            UpdateTime = null,
        };
        var third = new ProjectTask
        {
            Id = Guid.NewGuid(),
            CreateTime = new DateTime(2024, 1, 1, 2, 0, 0, DateTimeKind.Utc),
            UpdateTime = new DateTime(2024, 1, 1, 3, 0, 0, DateTimeKind.Utc),
        };

        var result = _service.GetNextPending([first, second, third]);

        Assert.Same(second, result);
    }
}
