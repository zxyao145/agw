using Agw.Shared.Tasks.Entities;

namespace Agw.Shared.Tasks;

public interface ITaskAppService
{
    Task<ProjectTask?> GetTaskAsync(Guid value);
    Task<bool> HasSessionAsync(string sessionId, string? projectId = null, CancellationToken cancellationToken = default);
    Task<bool> HasTaskAsync(string taskId, CancellationToken cancellationToken = default);
}
