using Agw.Shared.Enums;
using Agw.Shared.Tasks.Entities;

namespace Agw.Shared.Tasks;

public interface ITaskAppService
{
    Task<ProjectTask?> GetTaskAsync(Guid value);

    Task<ProjectTask?> CreateTaskForExecutionAsync(
        Guid projectId,
        Guid? taskId,
        AgentRuntimeType agentType,
        Guid executionId,
        string input,
        string user,
        CancellationToken cancellationToken = default);

    Task<bool> HasTaskAsync(Guid taskId, Guid? projectId = null, CancellationToken cancellationToken = default);
}
