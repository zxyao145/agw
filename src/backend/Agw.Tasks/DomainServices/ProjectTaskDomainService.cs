using Agw.Shared;
using Agw.Shared.Enums;
using Agw.Shared.Tasks.Entities;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Agw.Domain.Services;

public class ProjectTaskDomainService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool TryPrepareForCreate(
        ProjectTask task,
        TaskRecord initialRecord,
        string user,
        ProjectTaskStatus initialStatus = ProjectTaskStatus.Pending)
    {
        if (string.IsNullOrWhiteSpace(task.Description)
            || string.IsNullOrWhiteSpace(task.ContextId)
            || string.IsNullOrWhiteSpace(initialRecord.GetText()))
        {
            return false;
        }

        if (!task.AgentId.HasValue)
        {
            return false;
        }

        task.Id = task.Id == Guid.Empty ? Guid.NewGuid() : task.Id;
        task.Title = task.Title?.Trim() ?? string.Empty;
        task.Description = task.Description.Trim();
        task.Status = initialStatus;
        task.CreateBy = user;
        task.CreateTime = DateTime.UtcNow;
        task.UpdateBy = user;
        task.UpdateTime = task.CreateTime;

        initialRecord.Id = initialRecord.Id == Guid.Empty ? Guid.NewGuid() : initialRecord.Id;
        initialRecord.TaskId = task.Id;
        initialRecord.AgentName = Constants.DefaultAuthor;
        initialRecord.CreateTime = task.CreateTime;
        initialRecord.UpdateTime = task.CreateTime;
        return true;
    }

    public bool TryApplyUpdate(
        ProjectTask task,
        TaskRecord latestRecord,
        string description,
        string input,
        out TaskRecord? record)
    {
        record = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        task.Description = description?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(task.Description))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        record = new TaskRecord
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            AgentName = latestRecord.AgentName,
            ConversationSequence = (latestRecord.ConversationSequence ?? -1) + 1,
            ConversationPayload = JsonSerializer.Serialize(new ChatMessage(ChatRole.User, input.Trim()), JsonOptions),
            CreateTime = now,
            UpdateTime = now
        };

        task.UpdateTime = now;
        return true;
    }

    public bool TryUpdateTitle(ProjectTask task, string title, string user)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        task.Title = title.Trim();
        task.UpdateBy = user;
        task.UpdateTime = DateTime.UtcNow;
        return true;
    }

    public bool TryReorder(ProjectTask task, DateTime newUpdateTimeUtc, string user)
    {
        if (task.Status != ProjectTaskStatus.Pending)
        {
            return false;
        }

        task.UpdateBy = user;
        task.UpdateTime = newUpdateTimeUtc;
        return true;
    }

    public bool TryCancel(ProjectTask task, string user)
    {
        if (task.Status is not (ProjectTaskStatus.Pending or ProjectTaskStatus.Running))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        task.Status = ProjectTaskStatus.Canceled;
        task.UpdateBy = user;
        task.UpdateTime = now;
        task.FinishedTime = now;
        return true;
    }

    public bool TryMarkRunning(ProjectTask task, string user)
    {
        if (task.Status != ProjectTaskStatus.Pending)
        {
            return false;
        }

        task.Status = ProjectTaskStatus.Running;
        task.UpdateBy = user;
        task.UpdateTime = DateTime.UtcNow;
        return true;
    }

    public bool TryMarkSucceeded(ProjectTask task, string user)
    {
        if (task.Status != ProjectTaskStatus.Running)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        task.Status = ProjectTaskStatus.Succeeded;
        task.ErrorMessage = null;
        task.FinishedTime = now;
        task.UpdateBy = user;
        task.UpdateTime = now;
        return true;
    }

    public bool TryMarkFailed(ProjectTask task, string errorMessage, string user)
    {
        if (task.Status != ProjectTaskStatus.Running)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        task.Status = ProjectTaskStatus.Failed;
        task.ErrorMessage = errorMessage;
        task.FinishedTime = now;
        task.UpdateBy = user;
        task.UpdateTime = now;
        return true;
    }

    public ProjectTask? GetNextPending(IReadOnlyList<ProjectTask> pendingTasks) =>
        pendingTasks
            .OrderBy(t => t.UpdateTime ?? t.CreateTime)
            .ThenBy(t => t.CreateTime)
            .FirstOrDefault();
}
