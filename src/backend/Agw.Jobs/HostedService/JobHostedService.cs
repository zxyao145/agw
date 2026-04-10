using System.Collections.Concurrent;

using Agw.Jobs.Application.Services;
using Agw.Jobs.Domain.Entities;
using Agw.Jobs.Domain.Events;
using Agw.Jobs.Dtos;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Jobs.HostedService;

/// <summary>
/// In-memory scheduler backed by persistent task state in DB.
/// DB handles durability and coarse scheduling; memory queue handles precise execution.
/// </summary>
public class JobHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<JobHostedService> logger,
    IJobDomainEventDispatcher jobDomainEventDispatcher) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<JobHostedService> _logger = logger;
    private readonly IJobDomainEventDispatcher _jobDomainEventDispatcher = jobDomainEventDispatcher;

    private readonly PriorityQueue<InMemoryJob, DateTimeOffset> _queue = new();
    private readonly ConcurrentDictionary<Guid, InMemoryJob> _taskMap = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _prefetchSignal = new(0, int.MaxValue);

    private readonly TimeSpan _prefetchInterval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan _prefetchWindow = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _jobDomainEventDispatcher.DomainEventDispatched += HandleDomainEventAsync;

        try
        {
            var prefetchTask = RunPrefetchLoopAsync(stoppingToken);
            var executeTask = RunExecuteLoopAsync(stoppingToken);
            await Task.WhenAll(prefetchTask, executeTask);
        }
        finally
        {
            _jobDomainEventDispatcher.DomainEventDispatched -= HandleDomainEventAsync;
        }
    }

    private async Task RunPrefetchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var jobTaskStore = scope.ServiceProvider.GetRequiredService<IJobStore>();

                var now = DateTimeOffset.UtcNow;
                var tasks = await jobTaskStore.PrefetchAsync(now, now.Add(_prefetchWindow), cancellationToken);
                foreach (var task in tasks)
                {
                    UpsertInMemoryTask(task);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job prefetch loop failed.");
            }

            try
            {
                var delayTask = Task.Delay(_prefetchInterval, cancellationToken);
                var signalTask = _prefetchSignal.WaitAsync(cancellationToken);
                await Task.WhenAny(delayTask, signalTask);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }


    private Task HandleDomainEventAsync(IJobDomainEvent domainEvent, CancellationToken _)
    {
        if (domainEvent is not JobCreatedDomainEvent createdEvent)
        {
            return Task.CompletedTask;
        }

        var job = createdEvent.Job;
        var now = DateTimeOffset.UtcNow;
        if (job.TriggerType != Agw.Jobs.Domain.Enums.TriggerType.Once)
        {
            return Task.CompletedTask;
        }

        if (!job.IsEnabled || job.Status != Agw.Jobs.Domain.Enums.JobStatus.Pending)
        {
            return Task.CompletedTask;
        }

        if (job.NextRunTime >= now.Add(_prefetchInterval))
        {
            return Task.CompletedTask;
        }

        _prefetchSignal.Release();
        return Task.CompletedTask;
    }

    private async Task RunExecuteLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var hasNext = TryPeekLatest(out var nextTask);
            if (!hasNext || nextTask == null)
            {
                await _wakeSignal.WaitAsync(cancellationToken);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            if (nextTask.NextRunTime > now)
            {
                var delay = nextTask.NextRunTime - now;
                var delayTask = Task.Delay(delay, cancellationToken);
                var signalTask = _wakeSignal.WaitAsync(cancellationToken);
                await Task.WhenAny(delayTask, signalTask);
                continue;
            }

            if (!TryDequeueLatest(out var dequeued) || dequeued == null)
            {
                continue;
            }

            await ExecuteOneAsync(dequeued, cancellationToken);
        }
    }

    private async Task ExecuteOneAsync(InMemoryJob inMemoryTask, CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow;
        using var scope = _scopeFactory.CreateScope();
        var jobTaskStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var timeCalculator = scope.ServiceProvider.GetRequiredService<IJobTimeCalculator>();
        var agentExecutor = scope.ServiceProvider.GetRequiredService<IAgentExecutor>();

        try
        {
            var markedRunning = await jobTaskStore.MarkRunningAsync(inMemoryTask.JobId, cancellationToken);
            if (!markedRunning)
            {
                _logger.LogInformation(
                    "Job {JobId} is no longer enabled/pending. Dropping stale in-memory entry.",
                    inMemoryTask.JobId);
                _taskMap.TryRemove(inMemoryTask.JobId, out _);
                return;
            }

            var job = ToJob(inMemoryTask);
            await agentExecutor.ExecuteAsync(job, cancellationToken);

            var nextRunTime = timeCalculator.GetNextRunTime(job, DateTimeOffset.UtcNow);
            await jobTaskStore.MarkSucceededAsync(inMemoryTask.JobId, nextRunTime, cancellationToken);
            await jobTaskStore.AddExecutionLogAsync(
                inMemoryTask.JobId,
                start,
                DateTimeOffset.UtcNow,
                success: true,
                attempt: inMemoryTask.RetryCount + 1,
                errorMessage: null,
                cancellationToken);

            if (nextRunTime.HasValue)
            {
                var updatedTask = ToJob(inMemoryTask);
                updatedTask.NextRunTime = nextRunTime.Value;
                updatedTask.RetryCount = 0;
                UpsertInMemoryTask(updatedTask);
            }
            else
            {
                _taskMap.TryRemove(inMemoryTask.JobId, out _);
            }
        }
        catch (Exception ex)
        {
            if (IsMissingTaskException(ex))
            {
                _logger.LogWarning("Job {JobId} no longer exists. Dropping stale in-memory entry.", inMemoryTask.JobId);
                _taskMap.TryRemove(inMemoryTask.JobId, out _);
                return;
            }

            _logger.LogError(ex, "Job {JobId} execution failed.", inMemoryTask.JobId);
            var retryCount = inMemoryTask.RetryCount + 1;

            if (retryCount <= inMemoryTask.MaxRetryCount)
            {
                var nextRunTime = DateTimeOffset.UtcNow.Add(_retryDelay);
                try
                {
                    await jobTaskStore.MarkRetryAsync(inMemoryTask.JobId, nextRunTime, retryCount, ex.Message, cancellationToken);
                    await jobTaskStore.AddExecutionLogAsync(
                        inMemoryTask.JobId,
                        start,
                        DateTimeOffset.UtcNow,
                        success: false,
                        attempt: retryCount,
                        errorMessage: ex.Message,
                        cancellationToken);
                }
                catch (Exception bookkeepingEx) when (IsMissingTaskException(bookkeepingEx))
                {
                    _logger.LogWarning("Job {JobId} disappeared during retry bookkeeping. Dropping stale in-memory entry.", inMemoryTask.JobId);
                    _taskMap.TryRemove(inMemoryTask.JobId, out _);
                    return;
                }

                var updatedTask = ToJob(inMemoryTask);
                updatedTask.NextRunTime = nextRunTime;
                updatedTask.RetryCount = retryCount;
                UpsertInMemoryTask(updatedTask);
            }
            else
            {
                try
                {
                    await jobTaskStore.MarkFailedAsync(inMemoryTask.JobId, retryCount, ex.Message, cancellationToken);
                    await jobTaskStore.AddExecutionLogAsync(
                        inMemoryTask.JobId,
                        start,
                        DateTimeOffset.UtcNow,
                        success: false,
                        attempt: retryCount,
                        errorMessage: ex.Message,
                        cancellationToken);
                }
                catch (Exception bookkeepingEx) when (IsMissingTaskException(bookkeepingEx))
                {
                    _logger.LogWarning("Job {JobId} disappeared during failure bookkeeping. Dropping stale in-memory entry.", inMemoryTask.JobId);
                }

                _taskMap.TryRemove(inMemoryTask.JobId, out _);
            }
        }
    }

    private static bool IsMissingTaskException(Exception exception)
    {
        return exception is InvalidOperationException invalidOperationException
            && invalidOperationException.Message.StartsWith("Job not found:", StringComparison.Ordinal);
    }

    private void UpsertInMemoryTask(Job task)
    {
        InMemoryJob upserted;

        lock (_queueLock)
        {
            var version = _taskMap.TryGetValue(task.Id, out var existing)
                ? existing.Version + 1
                : 1;

            upserted = new InMemoryJob
            {
                JobId = task.Id,
                ProjectId = task.ProjectId,
                AgentType = task.AgentType,
                AgentId = task.AgentId,
                Name = task.Name,
                Prompt = task.Prompt,
                TriggerType = task.TriggerType,
                TriggerValue = task.TriggerValue,
                NextRunTime = task.NextRunTime,
                RetryCount = task.RetryCount,
                MaxRetryCount = task.MaxRetryCount,
                Version = version
            };

            _taskMap[task.Id] = upserted;
            _queue.Enqueue(upserted, upserted.NextRunTime);
        }

        _wakeSignal.Release();
    }

    private bool TryPeekLatest(out InMemoryJob? task)
    {
        lock (_queueLock)
        {
            while (_queue.TryPeek(out var candidate, out _))
            {
                if (_taskMap.TryGetValue(candidate.JobId, out var current) && current.Version == candidate.Version)
                {
                    task = candidate;
                    return true;
                }

                _queue.Dequeue();
            }
        }

        task = null;
        return false;
    }

    private bool TryDequeueLatest(out InMemoryJob? task)
    {
        lock (_queueLock)
        {
            while (_queue.TryDequeue(out var candidate, out _))
            {
                if (_taskMap.TryGetValue(candidate.JobId, out var current) && current.Version == candidate.Version)
                {
                    task = candidate;
                    return true;
                }
            }
        }

        task = null;
        return false;
    }

    private static Job ToJob(InMemoryJob inMemoryTask)
    {
        return new Job
        {
            Id = inMemoryTask.JobId,
            ProjectId = inMemoryTask.ProjectId,
            AgentType = inMemoryTask.AgentType,
            AgentId = inMemoryTask.AgentId,
            Name = inMemoryTask.Name,
            Prompt = inMemoryTask.Prompt,
            TriggerType = inMemoryTask.TriggerType,
            TriggerValue = inMemoryTask.TriggerValue,
            NextRunTime = inMemoryTask.NextRunTime,
            RetryCount = inMemoryTask.RetryCount,
            MaxRetryCount = inMemoryTask.MaxRetryCount
        };
    }
}
