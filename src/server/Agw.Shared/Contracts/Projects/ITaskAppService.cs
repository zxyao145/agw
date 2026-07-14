namespace Agw.Shared.Contracts.Projects;

public interface ITaskAppService
{
    Task<TaskProjection?> GetTaskAsync(Guid value);

    Task<TaskProjection?> CreateTaskForExecutionAsync(
        Guid projectId,
        Guid? taskId,
        string input,
        string user,
        string? contextId = null,
        CancellationToken cancellationToken = default);

    Task<bool> HasTaskAsync(Guid taskId, Guid? projectId = null, CancellationToken cancellationToken = default);


    Task<ExecutionTaskResolutionResult> ResolveTaskAsync(ExecutionTaskRequest request, CancellationToken cancellationToken);
}
