using Agw.Domain.Entities;
using Agw.Jobs.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Agw.Jobs.Services;

/// <summary>
/// In-memory scheduler backed by persistent task state in DB.
/// DB handles durability and coarse scheduling; memory queue handles precise execution.
/// </summary>
public class ScheduledTaskHostedService(
    IScheduledTaskStore scheduledTaskStore,
    IScheduledTaskTimeCalculator timeCalculator,
    IAgentExecutor agentExecutor,
    ILogger<ScheduledTaskHostedService> logger) : BackgroundService
{
    private readonly PriorityQueue<InMemoryScheduledTask, DateTimeOffset> _queue = new();
    private readonly ConcurrentDictionary<Guid, InMemoryScheduledTask> _taskMap = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    private readonly TimeSpan _prefetchInterval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan _prefetchWindow = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(30);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var prefetchTask = RunPrefetchLoopAsync(stoppingToken);
        var executeTask = RunExecuteLoopAsync(stoppingToken);
        return Task.WhenAll(prefetchTask, executeTask);
    }

    private async Task RunPrefetchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var tasks = await scheduledTaskStore.PrefetchAsync(now, now.Add(_prefetchWindow), cancellationToken);
                foreach (var task in tasks)
                {
                    UpsertInMemoryTask(task);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ScheduledTask prefetch loop failed.");
            }

            try
            {
                await Task.Delay(_prefetchInterval, cancellationToken);
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

    private async Task ExecuteOneAsync(InMemoryScheduledTask inMemoryTask, CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow;

        try
        {
            var markedRunning = await scheduledTaskStore.MarkRunningAsync(inMemoryTask.TaskId, cancellationToken);
            if (!markedRunning)
            {
                logger.LogInformation(
                    "Scheduled task {TaskId} is no longer enabled/pending. Dropping stale in-memory entry.",
                    inMemoryTask.TaskId);
                _taskMap.TryRemove(inMemoryTask.TaskId, out _);
                return;
            }

            var scheduledTask = ToScheduledTask(inMemoryTask);
            await agentExecutor.ExecuteAsync(scheduledTask, cancellationToken);

            var nextRunTime = timeCalculator.GetNextRunTime(scheduledTask, DateTimeOffset.UtcNow);
            await scheduledTaskStore.MarkSucceededAsync(inMemoryTask.TaskId, nextRunTime, cancellationToken);
            await scheduledTaskStore.AddExecutionLogAsync(
                inMemoryTask.TaskId,
                start,
                DateTimeOffset.UtcNow,
                success: true,
                attempt: inMemoryTask.RetryCount + 1,
                errorMessage: null,
                cancellationToken);

            if (nextRunTime.HasValue)
            {
                var updatedTask = ToScheduledTask(inMemoryTask);
                updatedTask.NextRunTime = nextRunTime.Value;
                updatedTask.RetryCount = 0;
                UpsertInMemoryTask(updatedTask);
            }
            else
            {
                _taskMap.TryRemove(inMemoryTask.TaskId, out _);
            }
        }
        catch (Exception ex)
        {
            if (IsMissingTaskException(ex))
            {
                logger.LogWarning("Scheduled task {TaskId} no longer exists. Dropping stale in-memory entry.", inMemoryTask.TaskId);
                _taskMap.TryRemove(inMemoryTask.TaskId, out _);
                return;
            }

            logger.LogError(ex, "Scheduled task {TaskId} execution failed.", inMemoryTask.TaskId);
            var retryCount = inMemoryTask.RetryCount + 1;

            if (retryCount <= inMemoryTask.MaxRetryCount)
            {
                var nextRunTime = DateTimeOffset.UtcNow.Add(_retryDelay);
                try
                {
                    await scheduledTaskStore.MarkRetryAsync(inMemoryTask.TaskId, nextRunTime, retryCount, ex.Message, cancellationToken);
                    await scheduledTaskStore.AddExecutionLogAsync(
                        inMemoryTask.TaskId,
                        start,
                        DateTimeOffset.UtcNow,
                        success: false,
                        attempt: retryCount,
                        errorMessage: ex.Message,
                        cancellationToken);
                }
                catch (Exception bookkeepingEx) when (IsMissingTaskException(bookkeepingEx))
                {
                    logger.LogWarning("Scheduled task {TaskId} disappeared during retry bookkeeping. Dropping stale in-memory entry.", inMemoryTask.TaskId);
                    _taskMap.TryRemove(inMemoryTask.TaskId, out _);
                    return;
                }

                var updatedTask = ToScheduledTask(inMemoryTask);
                updatedTask.NextRunTime = nextRunTime;
                updatedTask.RetryCount = retryCount;
                UpsertInMemoryTask(updatedTask);
            }
            else
            {
                try
                {
                    await scheduledTaskStore.MarkFailedAsync(inMemoryTask.TaskId, retryCount, ex.Message, cancellationToken);
                    await scheduledTaskStore.AddExecutionLogAsync(
                        inMemoryTask.TaskId,
                        start,
                        DateTimeOffset.UtcNow,
                        success: false,
                        attempt: retryCount,
                        errorMessage: ex.Message,
                        cancellationToken);
                }
                catch (Exception bookkeepingEx) when (IsMissingTaskException(bookkeepingEx))
                {
                    logger.LogWarning("Scheduled task {TaskId} disappeared during failure bookkeeping. Dropping stale in-memory entry.", inMemoryTask.TaskId);
                }

                _taskMap.TryRemove(inMemoryTask.TaskId, out _);
            }
        }
    }

    private static bool IsMissingTaskException(Exception exception)
    {
        return exception is InvalidOperationException invalidOperationException
            && invalidOperationException.Message.StartsWith("Scheduled task not found:", StringComparison.Ordinal);
    }

    private void UpsertInMemoryTask(ScheduledTask task)
    {
        InMemoryScheduledTask upserted;

        lock (_queueLock)
        {
            var version = _taskMap.TryGetValue(task.Id, out var existing)
                ? existing.Version + 1
                : 1;

            upserted = new InMemoryScheduledTask
            {
                TaskId = task.Id,
                ProjectId = task.ProjectId,
                AgentType = task.AgentType,
                AgentId = task.AgentId,
                Name = task.Name,
                Prompt = task.Prompt,
                TriggerType = task.TriggerType,
                TriggerValue = task.TriggerValue,
                TimeZoneId = task.TimeZoneId,
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

    private bool TryPeekLatest(out InMemoryScheduledTask? task)
    {
        lock (_queueLock)
        {
            while (_queue.TryPeek(out var candidate, out _))
            {
                if (_taskMap.TryGetValue(candidate.TaskId, out var current) && current.Version == candidate.Version)
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

    private bool TryDequeueLatest(out InMemoryScheduledTask? task)
    {
        lock (_queueLock)
        {
            while (_queue.TryDequeue(out var candidate, out _))
            {
                if (_taskMap.TryGetValue(candidate.TaskId, out var current) && current.Version == candidate.Version)
                {
                    task = candidate;
                    return true;
                }
            }
        }

        task = null;
        return false;
    }

    private static ScheduledTask ToScheduledTask(InMemoryScheduledTask inMemoryTask)
    {
        return new ScheduledTask
        {
            Id = inMemoryTask.TaskId,
            ProjectId = inMemoryTask.ProjectId,
            AgentType = inMemoryTask.AgentType,
            AgentId = inMemoryTask.AgentId,
            Name = inMemoryTask.Name,
            Prompt = inMemoryTask.Prompt,
            TriggerType = inMemoryTask.TriggerType,
            TriggerValue = inMemoryTask.TriggerValue,
            TimeZoneId = inMemoryTask.TimeZoneId,
            NextRunTime = inMemoryTask.NextRunTime,
            RetryCount = inMemoryTask.RetryCount,
            MaxRetryCount = inMemoryTask.MaxRetryCount
        };
    }
}
