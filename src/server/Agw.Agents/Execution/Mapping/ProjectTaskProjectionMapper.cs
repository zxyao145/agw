using Agw.Projects.Contracts.Execution;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Mapping;

internal static class ProjectTaskProjectionMapper
{
    public static TaskProjection Map(ProjectTaskSnapshot task) =>
        new()
        {
            TaskId = task.TaskId,
            ProjectConversationId = task.ProjectConversationId,
            ProjectId = task.ProjectId,
            ContextId = task.ContextId,
            JobId = task.JobId,
            Title = task.Title,
            Status = task.Status switch
            {
                ProjectTaskStatus.Pending => TaskExecutionStatus.Pending,
                ProjectTaskStatus.Running => TaskExecutionStatus.Running,
                ProjectTaskStatus.Succeeded => TaskExecutionStatus.Succeeded,
                ProjectTaskStatus.Failed => TaskExecutionStatus.Failed,
                ProjectTaskStatus.Canceled => TaskExecutionStatus.Canceled,
                _ => throw new AgwException(ErrorCodes.InvalidParam, $"Unsupported task status '{task.Status}'."),
            },
            ErrorMessage = task.ErrorMessage,
            CreateTime = task.CreatedAt,
            UpdateTime = task.UpdatedAt,
            FinishedTime = task.FinishedAt,
        };
}
