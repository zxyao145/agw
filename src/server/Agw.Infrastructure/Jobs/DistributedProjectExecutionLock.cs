using Agw.Jobs.Scheduling.Coordination;
using Medallion.Threading;

namespace Agw.Infrastructure.Jobs;

public sealed class DistributedProjectExecutionLock : IProjectExecutionLock
{
    private readonly IDistributedLockProvider _lockProvider;

    public DistributedProjectExecutionLock(IDistributedLockProvider lockProvider)
    {
        _lockProvider = lockProvider;
    }

    public async Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var lockName = $"agw:jobs:project-lock:{projectId:D}";
        return await _lockProvider.AcquireLockAsync(lockName, cancellationToken: cancellationToken);
    }
}
