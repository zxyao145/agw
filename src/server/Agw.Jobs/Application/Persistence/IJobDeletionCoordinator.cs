using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Application.Persistence;

public interface IJobDeletionCoordinator
{
    Task<bool> DeleteAsync(Guid id, Action<Job> ensureMutable, CancellationToken cancellationToken = default);

    Task<Job?> DeleteByProjectAsync(
        Guid id,
        Guid projectId,
        Action<Job> ensureMutable,
        CancellationToken cancellationToken = default
    );
}
