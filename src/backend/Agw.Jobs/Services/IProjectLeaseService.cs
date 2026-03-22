namespace Agw.Jobs.Services;

public interface IProjectLeaseService
{
    Task<bool> TryAcquireAsync(Guid projectId, string instanceId, CancellationToken cancellationToken);

    Task RenewAsync(Guid projectId, string instanceId, CancellationToken cancellationToken);

    Task ReleaseAsync(Guid projectId, string instanceId, CancellationToken cancellationToken);
}
