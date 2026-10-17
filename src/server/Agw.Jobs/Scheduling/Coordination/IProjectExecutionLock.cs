using Agw.Shared.Contracts.Coordination;

namespace Agw.Jobs.Scheduling.Coordination;

/// <summary>
/// Serializes scheduled job execution for the same project across the configured lock scope.
/// </summary>
public interface IProjectExecutionLock
{
    Task<IApplicationLockLease> AcquireAsync(Guid projectId, CancellationToken cancellationToken);

    Task<IApplicationLockLease?> TryAcquireAsync(Guid projectId, CancellationToken cancellationToken);
}
