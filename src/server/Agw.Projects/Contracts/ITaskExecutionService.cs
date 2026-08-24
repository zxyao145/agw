using Agw.Shared.Contracts.Projects;

namespace Agw.Projects.Contracts;

public interface ITaskExecutionService
{
    Task<ApplicationResult<TaskExecutionSnapshot>> CreateRunningForExecutionAsync(
        Guid projectId,
        Guid taskId,
        TaskCreateRequest request,
        string user
    );

    Task<TaskProjection?> GetTaskAsync(Guid id);

    Task<TaskProjection?> MarkSucceededAsync(Guid id, string user);

    Task<TaskProjection?> MarkFailedAsync(Guid id, string errorMessage, string user);
}
