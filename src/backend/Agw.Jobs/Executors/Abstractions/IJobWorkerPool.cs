using Agw.Jobs.Dtos;
using Agw.Jobs.Executors.Common;

namespace Agw.Jobs.Executors.Abstractions;

public interface IJobWorkerPool
{
    Task RegisterAsync(JobWorkerDescriptor worker, CancellationToken cancellationToken);

    Task HeartbeatAsync(JobWorkerDescriptor worker, CancellationToken cancellationToken);

    Task UnregisterAsync(string workerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<JobWorkerDescriptor>> ListAvailableWorkersAsync(CancellationToken cancellationToken);

    Task<JobWorkerDispatchResult> DispatchAsync(JobWorkerDescriptor worker, InMemoryJob job, CancellationToken cancellationToken);
}
