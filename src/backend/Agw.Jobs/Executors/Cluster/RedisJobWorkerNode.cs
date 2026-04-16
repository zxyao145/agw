using System.Collections.Concurrent;
using System.Text.Json;

using Agw.Jobs.Executors.Abstractions;
using Agw.Jobs.Executors.Common;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace Agw.Jobs.Executors.Cluster;

public sealed class RedisJobWorkerNode(
    IConnectionMultiplexer connectionMultiplexer,
    IJobWorkerPool workerPool,
    IJobWorker worker,
    IOptions<JobWorkerPoolOptions> options,
    ILogger<RedisJobWorkerNode> logger) : IJobWorkerNode
{
    private readonly IDatabase _database = connectionMultiplexer.GetDatabase();
    private readonly IJobWorkerPool _workerPool = workerPool;
    private readonly IJobWorker _worker = worker;
    private readonly JobWorkerPoolOptions _options = options.Value;
    private readonly ILogger<RedisJobWorkerNode> _logger = logger;
    private readonly ConcurrentDictionary<string, Task> _runningDispatches = new(StringComparer.Ordinal);
    private JobWorkerDescriptor? _workerDescriptor;

    public async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var workerId = GetWorkerId();
        _workerDescriptor = new JobWorkerDescriptor(
            workerId,
            GetNodeId(),
            $"agw:jobs:workers:{workerId}:queue",
            now,
            now,
            _options.MaxConcurrentJobs);

        await _workerPool.RegisterAsync(_workerDescriptor, cancellationToken);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_workerDescriptor == null)
        {
            await RegisterAsync(cancellationToken);
        }

        var heartbeatTask = RunHeartbeatLoopAsync(cancellationToken);
        var consumeTask = RunConsumeLoopAsync(cancellationToken);
        await Task.WhenAll(heartbeatTask, consumeTask);
    }

    public async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        if (_workerDescriptor == null)
        {
            return;
        }

        await _workerPool.UnregisterAsync(_workerDescriptor.WorkerId, cancellationToken);
        _workerDescriptor = null;
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_workerDescriptor != null)
            {
                await _workerPool.HeartbeatAsync(_workerDescriptor, cancellationToken);
            }

            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunConsumeLoopAsync(CancellationToken cancellationToken)
    {
        if (_workerDescriptor == null)
        {
            return;
        }

        using var concurrency = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentJobs));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await concurrency.WaitAsync(cancellationToken);
                var payload = await _database.ListRightPopAsync(_workerDescriptor.QueueName);
                if (payload.IsNull)
                {
                    concurrency.Release();
                    await Task.Delay(_options.QueuePollInterval, cancellationToken);
                    continue;
                }

                var dispatchPayload = payload.ToString();
                var dispatchTask = ProcessDispatchAsync(dispatchPayload, cancellationToken)
                    .ContinueWith(
                        (task, state) =>
                        {
                            concurrency.Release();
                            _runningDispatches.TryRemove(state as string ?? string.Empty, out _);
                            if (task.IsFaulted)
                            {
                                _logger.LogError(task.Exception, "Redis job dispatch processing failed.");
                            }
                        },
                        dispatchPayload,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                _runningDispatches[dispatchPayload] = dispatchTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis job worker consume loop failed.");
                await Task.Delay(_options.QueuePollInterval, cancellationToken);
            }
        }

        if (!_runningDispatches.IsEmpty)
        {
            await Task.WhenAll(_runningDispatches.Values);
        }
    }

    private async Task ProcessDispatchAsync(string payload, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<RedisJobDispatchMessage>(payload);
        if (message == null || _workerDescriptor == null)
        {
            return;
        }

        RedisJobDispatchResponse response;
        try
        {
            var executionResult = await _worker.ExecuteAsync(message.Job, cancellationToken);
            response = new RedisJobDispatchResponse
            {
                DispatchId = message.DispatchId,
                WorkerId = _workerDescriptor.WorkerId,
                JobId = message.Job.JobId,
                Succeeded = true,
                ExecutionResult = executionResult
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Redis job worker {WorkerId} failed dispatch {DispatchId}.", _workerDescriptor.WorkerId, message.DispatchId);
            response = new RedisJobDispatchResponse
            {
                DispatchId = message.DispatchId,
                WorkerId = _workerDescriptor.WorkerId,
                JobId = message.Job.JobId,
                Succeeded = false,
                ErrorMessage = ex.Message
            };
        }

        await _database.ListLeftPushAsync(message.ResultQueueKey, JsonSerializer.Serialize(response));
        await _database.KeyExpireAsync(message.ResultQueueKey, _options.DispatchResultTtl);
    }

    private string GetWorkerId()
    {
        if (!string.IsNullOrWhiteSpace(_options.WorkerId))
        {
            return _options.WorkerId;
        }

        return $"{Environment.MachineName}-{Environment.ProcessId}";
    }

    private string GetNodeId()
    {
        return string.IsNullOrWhiteSpace(_options.NodeId)
            ? Environment.MachineName
            : _options.NodeId;
    }
}
