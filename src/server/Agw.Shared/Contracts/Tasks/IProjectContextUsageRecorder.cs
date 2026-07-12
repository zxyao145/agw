using Agw.Shared.Data.Entities.Tasks;

namespace Agw.Shared.Contracts.Tasks;

public interface IProjectContextUsageRecorder
{
    Task AddAsync(
        Guid projectId,
        string contextId,
        ProjectContextUsage usage,
        CancellationToken cancellationToken = default);
}
