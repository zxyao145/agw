using Agw.Shared.Enums;
using Agw.Shared.Tasks.Entities;

namespace Agw.Shared.Tasks;

public interface ITaskAppService
{
    Task<ProjectTask?> GetTaskAsync(Guid value);

    Task<ProjectTask?> CreateTaskForExecutionAsync(
        Guid projectId,
        Guid? taskId,
        string input,
        string user,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ProjectTask?>(
            new NotSupportedException("This task service implementation does not support session-based task creation."));

    [Obsolete("Project tasks no longer persist task-level target bindings.")]
    Task<ProjectTask?> CreateTaskForExecutionAsync(
        Guid projectId,
        Guid? taskId,
        AgentRuntimeType agentType,
        Guid executionId,
        string input,
        string user,
        CancellationToken cancellationToken = default) =>
        CreateTaskForExecutionAsync(projectId, taskId, input, user, cancellationToken);

    Task<bool> HasTaskAsync(Guid taskId, Guid? projectId = null, CancellationToken cancellationToken = default);
}
