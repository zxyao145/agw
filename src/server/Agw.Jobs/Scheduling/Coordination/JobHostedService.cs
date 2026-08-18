using System.Collections.Concurrent;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Jobs.Scheduling.Coordination;

/// <summary>
/// Coordinates database prefetch, precise in-memory scheduling, and serial dispatch by project.
/// Attempt state transitions and execution bookkeeping are delegated to <see cref="JobAttemptRunner"/>.
/// </summary>
public sealed class JobHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobHostedService> _logger;
    private readonly JobSchedulerWakeSignal _schedulerWakeSignal;
    private readonly IProjectExecutionLock _projectExecutionLock;
    private readonly IServerInitializationState _serverInitializationState;
    private readonly TimeProvider _timeProvider;

    private readonly PriorityQueue<ScheduledJob, DateTimeOffset> _queue = new();
    private readonly ConcurrentDictionary<Guid, ScheduledJob> _jobMap = new();
    private readonly ConcurrentDictionary<Guid, byte> _runningProjects = new();
    private readonly Dictionary<Guid, Queue<ScheduledJob>> _projectBacklog = new();
    private readonly ConcurrentDictionary<Guid, Task> _runningExecutions = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    public JobHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<JobHostedService> logger,
        JobSchedulerWakeSignal schedulerWakeSignal,
        IProjectExecutionLock projectExecutionLock,
        IServerInitializationState serverInitializationState,
        TimeProvider timeProvider
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _schedulerWakeSignal = schedulerWakeSignal;
        _projectExecutionLock = projectExecutionLock;
        _serverInitializationState = serverInitializationState;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_serverInitializationState.IsInitialized)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, stoppingToken);
        }

        var prefetchTask = RunPrefetchLoopAsync(stoppingToken);
        var executeTask = RunExecuteLoopAsync(stoppingToken);
        await Task.WhenAll(prefetchTask, executeTask);
    }

    private async Task RunPrefetchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();

                var now = _timeProvider.GetUtcNow();
                var jobs = await jobStore.PrefetchAsync(
                    now,
                    now.Add(JobSchedulingDefaults.PrefetchWindow),
                    cancellationToken
                );
                foreach (var job in jobs)
                {
                    UpsertScheduledJob(job);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job prefetch loop failed.");
            }

            try
            {
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delayTask = Task.Delay(
                    JobSchedulingDefaults.PrefetchInterval,
                    _timeProvider,
                    waitCancellation.Token
                );
                var signalTask = _schedulerWakeSignal.WaitAsync(waitCancellation.Token);
                var completedTask = await Task.WhenAny(delayTask, signalTask);
                waitCancellation.Cancel();
                await completedTask;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunExecuteLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var hasNext = TryPeekLatest(out var nextJob);
            if (!hasNext || nextJob == null)
            {
                await _wakeSignal.WaitAsync(cancellationToken);
                continue;
            }

            var now = _timeProvider.GetUtcNow();
            if (nextJob.NextRunTime > now)
            {
                var delay = nextJob.NextRunTime - now;
                var delayTask = Task.Delay(delay, _timeProvider, cancellationToken);
                var signalTask = _wakeSignal.WaitAsync(cancellationToken);
                await Task.WhenAny(delayTask, signalTask);
                continue;
            }

            if (!TryDequeueLatest(out var dequeued) || dequeued == null)
            {
                continue;
            }

            DispatchOrQueueByProject(dequeued, cancellationToken);
        }
    }

    private void DispatchOrQueueByProject(ScheduledJob scheduledJob, CancellationToken cancellationToken)
    {
        var shouldStartExecution = false;

        lock (_queueLock)
        {
            if (_runningProjects.TryAdd(scheduledJob.ProjectId, 0))
            {
                shouldStartExecution = true;
            }
            else
            {
                if (!_projectBacklog.TryGetValue(scheduledJob.ProjectId, out var backlogQueue))
                {
                    backlogQueue = new Queue<ScheduledJob>();
                    _projectBacklog[scheduledJob.ProjectId] = backlogQueue;
                }

                backlogQueue.Enqueue(scheduledJob);
            }
        }

        if (!shouldStartExecution)
        {
            return;
        }

        StartProjectExecution(scheduledJob, cancellationToken);
    }

    private void StartProjectExecution(ScheduledJob scheduledJob, CancellationToken cancellationToken)
    {
        var executionTask = ExecuteProjectQueueAsync(scheduledJob, cancellationToken);
        _runningExecutions[scheduledJob.JobId] = executionTask;

        _ = executionTask.ContinueWith(
            task =>
            {
                _runningExecutions.TryRemove(scheduledJob.JobId, out _);
                if (task.IsFaulted)
                {
                    _logger.LogError(
                        task.Exception,
                        "Project queue for job {JobId} failed unexpectedly.",
                        scheduledJob.JobId
                    );
                }
            },
            TaskScheduler.Default
        );
    }

    private async Task ExecuteProjectQueueAsync(ScheduledJob scheduledJob, CancellationToken cancellationToken)
    {
        var current = scheduledJob;
        while (!cancellationToken.IsCancellationRequested)
        {
            await ExecuteOneAsync(current, cancellationToken);

            ScheduledJob? next = null;
            lock (_queueLock)
            {
                if (_projectBacklog.TryGetValue(current.ProjectId, out var backlogQueue))
                {
                    while (backlogQueue.Count > 0)
                    {
                        var candidate = backlogQueue.Dequeue();
                        if (_jobMap.TryGetValue(candidate.JobId, out var latest) && latest.Version == candidate.Version)
                        {
                            next = candidate;
                            break;
                        }
                    }

                    if (backlogQueue.Count == 0)
                    {
                        _projectBacklog.Remove(current.ProjectId);
                    }
                }

                if (next == null)
                {
                    _runningProjects.TryRemove(current.ProjectId, out _);
                    return;
                }
            }

            current = next;
        }
    }

    private async Task ExecuteOneAsync(ScheduledJob scheduledJob, CancellationToken cancellationToken)
    {
        await using var projectLock = await _projectExecutionLock.AcquireAsync(
            scheduledJob.ProjectId,
            cancellationToken
        );

        using var scope = _scopeFactory.CreateScope();
        var attemptRunner = scope.ServiceProvider.GetRequiredService<JobAttemptRunner>();
        var result = await attemptRunner.RunAsync(scheduledJob, cancellationToken);

        if (result is JobAttemptResult.Reschedule reschedule)
        {
            UpsertScheduledJob(reschedule.Job);
            return;
        }

        _jobMap.TryRemove(scheduledJob.JobId, out _);
    }

    private void UpsertScheduledJob(Job job)
    {
        UpsertScheduledJob(
            new ScheduledJob
            {
                JobId = job.Id,
                ProjectId = job.ProjectId,
                AgentType = job.AgentType,
                AgentId = job.AgentId,
                Name = job.Name,
                Prompt = job.Prompt,
                TriggerType = job.TriggerType,
                TriggerValue = job.TriggerValue,
                NextRunTime = job.NextRunTime,
                RetryCount = job.RetryCount,
                MaxRetryCount = job.MaxRetryCount,
            }
        );
    }

    private void UpsertScheduledJob(ScheduledJob scheduledJob)
    {
        ScheduledJob upserted;

        lock (_queueLock)
        {
            var version = _jobMap.TryGetValue(scheduledJob.JobId, out var existing) ? existing.Version + 1 : 1;

            upserted = scheduledJob with { Version = version };
            _jobMap[scheduledJob.JobId] = upserted;
            _queue.Enqueue(upserted, upserted.NextRunTime);
        }

        _wakeSignal.Release();
    }

    private bool TryPeekLatest(out ScheduledJob? scheduledJob)
    {
        lock (_queueLock)
        {
            while (_queue.TryPeek(out var candidate, out _))
            {
                if (_jobMap.TryGetValue(candidate.JobId, out var current) && current.Version == candidate.Version)
                {
                    scheduledJob = candidate;
                    return true;
                }

                _queue.Dequeue();
            }
        }

        scheduledJob = null;
        return false;
    }

    private bool TryDequeueLatest(out ScheduledJob? scheduledJob)
    {
        lock (_queueLock)
        {
            while (_queue.TryDequeue(out var candidate, out _))
            {
                if (_jobMap.TryGetValue(candidate.JobId, out var current) && current.Version == candidate.Version)
                {
                    scheduledJob = candidate;
                    return true;
                }
            }
        }

        scheduledJob = null;
        return false;
    }
}
