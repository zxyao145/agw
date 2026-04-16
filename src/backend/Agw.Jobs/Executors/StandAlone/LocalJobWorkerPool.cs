using System.Collections.Concurrent;

using Agw.Jobs.Dtos;
using Agw.Jobs.Executors.Abstractions;
using Agw.Jobs.Executors.Common;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.Logging;

namespace Agw.Jobs.Executors.StandAlone;

public sealed class LocalJobWorkerPool(
    IJobWorker worker,
    ILogger<LocalJobWorkerPool> logger) : IJobWorkerPool
{
    private readonly IJobWorker _worker = worker;
    private readonly ILogger<LocalJobWorkerPool> _logger = logger;
    private readonly ConcurrentDictionary<string, JobWorkerDescriptor> _workers = new(StringComparer.Ordinal);

    public Task RegisterAsync(JobWorkerDescriptor worker, CancellationToken cancellationToken)
    {
        _workers[worker.WorkerId] = worker;
        _logger.LogInformation("Registered local job worker {WorkerId}.", worker.WorkerId);
        return Task.CompletedTask;
    }

    public Task HeartbeatAsync(JobWorkerDescriptor worker, CancellationToken cancellationToken)
    {
        _workers[worker.WorkerId] = worker with { LastSeenAt = DateTimeOffset.UtcNow };
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string workerId, CancellationToken cancellationToken)
    {
        _workers.TryRemove(workerId, out _);
        _logger.LogInformation("Unregistered local job worker {WorkerId}.", workerId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JobWorkerDescriptor>> ListAvailableWorkersAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<JobWorkerDescriptor>>(
            _workers.Values.OrderBy(worker => worker.WorkerId, StringComparer.Ordinal).ToList());
    }

    public async Task<JobWorkerDispatchResult> DispatchAsync(JobWorkerDescriptor worker, InMemoryJob job, CancellationToken cancellationToken)
    {
        if (!_workers.ContainsKey(worker.WorkerId))
        {
            throw new AgwException(ErrorCodes.JobWorkerUnavailable, $"Job worker is not registered: {worker.WorkerId}");
        }

        var executionResult = await _worker.ExecuteAsync(job, cancellationToken);
        return new JobWorkerDispatchResult(worker.WorkerId, job.JobId, executionResult);
    }
}
