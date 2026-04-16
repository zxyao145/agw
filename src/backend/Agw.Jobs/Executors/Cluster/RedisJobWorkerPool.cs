using System.Text.Json;

using Agw.Jobs.Dtos;
using Agw.Jobs.Executors.Abstractions;
using Agw.Jobs.Executors.Common;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace Agw.Jobs.Executors.Cluster;

public sealed class RedisJobWorkerPool : IJobWorkerPool
{
    private const string WorkerSetKey = "agw:jobs:workers";
    private const string WorkerKeyPrefix = "agw:jobs:workers:";
    private const string DispatchResultPrefix = "agw:jobs:dispatch:result:";

    private readonly IDatabase _database;
    private readonly JobWorkerPoolOptions _options;
    private readonly ILogger<RedisJobWorkerPool> _logger;

    public RedisJobWorkerPool(
        IConnectionMultiplexer connectionMultiplexer,
        IOptions<JobWorkerPoolOptions> options,
        ILogger<RedisJobWorkerPool> logger)
    {
        _database = connectionMultiplexer.GetDatabase();
        _options = options.Value;
        _logger = logger;
    }

    public async Task RegisterAsync(JobWorkerDescriptor worker, CancellationToken cancellationToken)
    {
        var registeredWorker = worker with { LastSeenAt = DateTimeOffset.UtcNow };
        await StoreWorkerAsync(registeredWorker);
        _logger.LogInformation("Registered Redis job worker {WorkerId}.", registeredWorker.WorkerId);
    }

    public Task HeartbeatAsync(JobWorkerDescriptor worker, CancellationToken cancellationToken)
    {
        return StoreWorkerAsync(worker with { LastSeenAt = DateTimeOffset.UtcNow });
    }

    public async Task UnregisterAsync(string workerId, CancellationToken cancellationToken)
    {
        await _database.KeyDeleteAsync(GetWorkerKey(workerId));
        await _database.SetRemoveAsync(WorkerSetKey, workerId);
        _logger.LogInformation("Unregistered Redis job worker {WorkerId}.", workerId);
    }

    public async Task<IReadOnlyList<JobWorkerDescriptor>> ListAvailableWorkersAsync(CancellationToken cancellationToken)
    {
        var workerIds = await _database.SetMembersAsync(WorkerSetKey);
        var workers = new List<JobWorkerDescriptor>();
        var now = DateTimeOffset.UtcNow;

        foreach (var workerId in workerIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (workerId.IsNull)
            {
                continue;
            }

            var workerIdText = workerId.ToString();
            var workerJson = await _database.StringGetAsync(GetWorkerKey(workerIdText));
            if (workerJson.IsNull)
            {
                await _database.SetRemoveAsync(WorkerSetKey, workerId);
                continue;
            }

            var worker = JsonSerializer.Deserialize<JobWorkerDescriptor>(workerJson.ToString());
            if (worker == null || worker.LastSeenAt.Add(_options.WorkerTimeout) < now)
            {
                await _database.SetRemoveAsync(WorkerSetKey, workerId);
                await _database.KeyDeleteAsync(GetWorkerKey(workerIdText));
                continue;
            }

            workers.Add(worker);
        }

        return workers
            .OrderBy(worker => worker.WorkerId, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<JobWorkerDispatchResult> DispatchAsync(JobWorkerDescriptor worker, InMemoryJob job, CancellationToken cancellationToken)
    {
        var dispatchId = Guid.NewGuid().ToString("N");
        var resultQueueKey = DispatchResultPrefix + dispatchId;
        var message = new RedisJobDispatchMessage
        {
            DispatchId = dispatchId,
            ResultQueueKey = resultQueueKey,
            Job = job
        };

        await _database.ListLeftPushAsync(worker.QueueName, JsonSerializer.Serialize(message));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var responseValue = await _database.ListRightPopAsync(resultQueueKey);
                if (!responseValue.IsNull)
                {
                    var response = JsonSerializer.Deserialize<RedisJobDispatchResponse>(responseValue.ToString());
                    if (response == null)
                    {
                        throw new AgwException(ErrorCodes.JobWorkerDispatchFailed, $"Job worker returned an invalid dispatch response: {dispatchId}");
                    }

                    if (!response.Succeeded)
                    {
                        throw new AgwException(
                            ErrorCodes.JobWorkerDispatchFailed,
                            response.ErrorMessage ?? $"Job worker dispatch failed: {dispatchId}");
                    }

                    return new JobWorkerDispatchResult(response.WorkerId, response.JobId, response.ExecutionResult);
                }

                await Task.Delay(_options.DispatchPollInterval, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new AgwException(ErrorCodes.JobWorkerDispatchFailed, $"Job worker dispatch ended before a response was received: {dispatchId}");
        }
        finally
        {
            await _database.KeyDeleteAsync(resultQueueKey);
        }
    }

    private async Task StoreWorkerAsync(JobWorkerDescriptor worker)
    {
        await _database.StringSetAsync(
            GetWorkerKey(worker.WorkerId),
            JsonSerializer.Serialize(worker),
            _options.WorkerTimeout);
        await _database.SetAddAsync(WorkerSetKey, worker.WorkerId);
    }

    private static string GetWorkerKey(string workerId)
    {
        return WorkerKeyPrefix + workerId;
    }
}
