using Agw.Shared;
using Agw.Shared.Tasks.Entities;
using Agw.Shared.Enums;

namespace Agw.Domain.Services;

public class ProjectTaskDomainService
{
    public bool TryPrepareForCreate(
        ProjectTask task,
        TaskRecord initialRecord,
        string user,
        ProjectTaskStatus initialStatus = ProjectTaskStatus.Pending)
    {
        if (string.IsNullOrWhiteSpace(task.ContextId)
            || string.IsNullOrWhiteSpace(initialRecord.GetText()))
        {
            return false;
        }

        task.Id = task.Id == Guid.Empty ? Guid.NewGuid() : task.Id;
        task.Title = string.IsNullOrWhiteSpace(task.Title) ? "Untitled" : task.Title.Trim();
        task.Status = initialStatus;
        task.CreateBy = user;
        task.CreateTime = DateTime.UtcNow;
        task.UpdateBy = user;
        task.UpdateTime = task.CreateTime;

        initialRecord.TaskId = task.Id;
        initialRecord.AgentName = Constants.DefaultAuthor;
        initialRecord.CreateTime = task.CreateTime;
        initialRecord.UpdateTime = task.CreateTime;
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
}
