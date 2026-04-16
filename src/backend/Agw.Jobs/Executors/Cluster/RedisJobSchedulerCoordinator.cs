using Agw.Jobs.Executors.Abstractions;
using Agw.Shared.Redis;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace Agw.Jobs.Executors.Cluster;

public sealed class RedisJobSchedulerCoordinator : IJobSchedulerCoordinator
{
    private const string SchedulerLockKey = "agw:jobs:scheduler:leader";

    private readonly RedisLock _redisLock;
    private readonly ILogger<RedisJobSchedulerCoordinator> _logger;

    public RedisJobSchedulerCoordinator(
        IConnectionMultiplexer connectionMultiplexer,
        IOptions<JobWorkerPoolOptions> options,
        ILogger<RedisJobSchedulerCoordinator> logger)
    {
        var workerPoolOptions = options.Value;
        _redisLock = new RedisLock(
            connectionMultiplexer,
            workerPoolOptions.SchedulerLockTtl,
            workerPoolOptions.SchedulerLockRetryDelay,
            workerPoolOptions.SchedulerLockRenewInterval);
        _logger = logger;
    }

    public async Task RunAsync(Func<CancellationToken, Task> scheduler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var lease = await _redisLock.AcquireLeaseAsync(SchedulerLockKey, cancellationToken);
                _logger.LogInformation("Acquired job scheduler leadership.");
                using var schedulerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var schedulerTask = scheduler(schedulerCancellation.Token);
                var completedTask = await Task.WhenAny(schedulerTask, lease.Lost);

                if (completedTask == lease.Lost)
                {
                    await schedulerCancellation.CancelAsync();
                    try
                    {
                        await schedulerTask;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The scheduler was stopped because this node lost leadership.
                    }

                    await lease.Lost;
                }

                await schedulerTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job scheduler leadership loop failed.");
            }
        }
    }
}
