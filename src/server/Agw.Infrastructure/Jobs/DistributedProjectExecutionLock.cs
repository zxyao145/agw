using Agw.Infrastructure.Coordination;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Contracts.Coordination;
using Medallion.Threading;

namespace Agw.Infrastructure.Jobs;

public sealed class DistributedProjectExecutionLock : IProjectExecutionLock
{
    private readonly IDistributedLockProvider _lockProvider;

    public DistributedProjectExecutionLock(IDistributedLockProvider lockProvider)
    {
        _lockProvider = lockProvider;
    }

    public async Task<IApplicationLockLease> AcquireAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var lockName = GetResourceName(projectId);
        var handle = await _lockProvider.AcquireLockAsync(lockName, cancellationToken: cancellationToken);
        return new DistributedSynchronizationLease(handle);
    }

    public async Task<IApplicationLockLease?> TryAcquireAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var lockName = GetResourceName(projectId);
        var handle = await _lockProvider.TryAcquireLockAsync(lockName, TimeSpan.Zero, cancellationToken);
        return handle == null ? null : new DistributedSynchronizationLease(handle);
    }

    private static string GetResourceName(Guid projectId) => $"agw:jobs:project-lock:{projectId:D}";
}
