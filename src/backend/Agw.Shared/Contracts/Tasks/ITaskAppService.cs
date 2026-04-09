using Agw.Shared.Data.Entities.Tasks;

namespace Agw.Shared.Contracts.Tasks;

public interface ITaskAppService
{
    Task<ProjectTask?> GetTaskAsync(Guid value);

    Task<ProjectTask?> CreateTaskForExecutionAsync(
        Guid projectId,
        Guid? taskId,
        string input,
        string user,
        CancellationToken cancellationToken = default);

    Task<bool> HasTaskAsync(Guid taskId, Guid? projectId = null, CancellationToken cancellationToken = default);
}
