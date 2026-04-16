using System.Collections.Concurrent;

using Agw.Jobs.Application.Services;
using Agw.Jobs.Domain.Entities;
using Agw.Jobs.Domain.Enums;
using Agw.Jobs.Domain.Events;
using Agw.Jobs.Dtos;
using Agw.Jobs.Executors.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Jobs.Executors.Common;

/// <summary>
/// Maintains the in-memory due-job queue and dispatches work to registered workers.
/// The scheduler owns project lanes, so jobs for the same ProjectId are dispatched serially.
/// </summary>
public sealed class JobScheduler(
    IServiceScopeFactory scopeFactory,
    IJobWorkerPool workerPool,
    IJobDomainEventDispatcher jobDomainEventDispatcher,
    IOptions<JobSchedulerOptions> options,
    ILogger<JobScheduler> logger) : IJobScheduler
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IJobWorkerPool _workerPool = workerPool;
    private readonly IJobDomainEventDispatcher _jobDomainEventDispatcher = jobDomainEventDispatcher;
    private readonly JobSchedulerOptions _options = options.Value;
    private readonly ILogger<JobScheduler> _logger = logger;

    private readonly PriorityQueue<InMemoryJob, DateTimeOffset> _queue = new();
    private readonly ConcurrentDictionary<Guid, InMemoryJob> _taskMap = new();
    private readonly ConcurrentDictionary<Guid, byte> _runningProjects = new();
    private readonly Dictionary<Guid, Queue<InMemoryJob>> _projectBacklog = new();
    private readonly ConcurrentDictionary<Guid, Task> _runningExecutions = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _prefetchSignal = new(0, int.MaxValue);

    private long _workerSelectionCursor = -1;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _jobDomainEventDispatcher.DomainEventDispatched += HandleDomainEventAsync;

        try
        {
            var prefetchTask = RunPrefetchLoopAsync(cancellationToken);
            var dispatchTask = RunDispatchLoopAsync(cancellationToken);
            await Task.WhenAll(prefetchTask, dispatchTask);
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
                var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();

                var now = DateTimeOffset.UtcNow;
                var jobs = await jobStore.PrefetchAsync(now, now.Add(_options.PrefetchWindow), cancellationToken);
                foreach (var job in jobs)
                {
                    UpsertInMemoryTask(job);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job prefetch loop failed.");
            }

            try
            {
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delayTask = Task.Delay(_options.PrefetchInterval, waitCancellation.Token);
                var signalTask = _prefetchSignal.WaitAsync(waitCancellation.Token);
                var completedTask = await Task.WhenAny(delayTask, signalTask);
                await waitCancellation.CancelAsync();
                await completedTask;
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
        if (job.TriggerType != TriggerType.Once)
        {
            return Task.CompletedTask;
        }

        if (!job.IsEnabled || job.Status != JobStatus.Pending)
        {
            return Task.CompletedTask;
        }

        if (job.NextRunTime >= now.Add(_options.PrefetchInterval))
        {
            return Task.CompletedTask;
        }

        _prefetchSignal.Release();
        return Task.CompletedTask;
    }

    private async Task RunDispatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var hasNext = TryPeekLatest(out var nextJob);
                if (!hasNext || nextJob == null)
                {
                    await _wakeSignal.WaitAsync(cancellationToken);
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                if (nextJob.NextRunTime > now)
                {
                    var delay = nextJob.NextRunTime - now;
                    var delayTask = Task.Delay(delay, cancellationToken);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job dispatch loop failed.");
            }
        }
    }

    private void DispatchOrQueueByProject(InMemoryJob job, CancellationToken cancellationToken)
    {
        var shouldStartExecution = false;

        lock (_queueLock)
        {
            if (_runningProjects.TryAdd(job.ProjectId, 0))
            {
                shouldStartExecution = true;
            }
            else
            {
                if (!_projectBacklog.TryGetValue(job.ProjectId, out var backlogQueue))
                {
                    backlogQueue = new Queue<InMemoryJob>();
                    _projectBacklog[job.ProjectId] = backlogQueue;
                }

                backlogQueue.Enqueue(job);
            }
        }

        if (!shouldStartExecution)
        {
            return;
        }

        StartProjectExecution(job, cancellationToken);
    }

    private void StartProjectExecution(InMemoryJob job, CancellationToken cancellationToken)
    {
        var executionTask = ExecuteProjectQueueAsync(job, cancellationToken);
        _runningExecutions[job.JobId] = executionTask;

        _ = executionTask.ContinueWith(
            task =>
            {
                _runningExecutions.TryRemove(job.JobId, out _);
                if (task.IsFaulted)
                {
                    _logger.LogError(task.Exception, "Project queue for job {JobId} failed unexpectedly.", job.JobId);
                }
            },
            TaskScheduler.Default);
    }

    private async Task ExecuteProjectQueueAsync(InMemoryJob job, CancellationToken cancellationToken)
    {
        var current = job;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var dispatchResult = await DispatchToSelectedWorkerAsync(current, cancellationToken);
                ApplyExecutionResult(current, dispatchResult.ExecutionResult);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Job {JobId} dispatch failed.", current.JobId);
                RequeueAfterDispatchFailure(current);
                ReleaseProjectLane(current.ProjectId);
                return;
            }

            InMemoryJob? next = null;
            lock (_queueLock)
            {
                if (_projectBacklog.TryGetValue(current.ProjectId, out var backlogQueue))
                {
                    while (backlogQueue.Count > 0)
                    {
                        var candidate = backlogQueue.Dequeue();
                        if (_taskMap.TryGetValue(candidate.JobId, out var latest) && latest.Version == candidate.Version)
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

    private async Task<JobWorkerDispatchResult> DispatchToSelectedWorkerAsync(InMemoryJob job, CancellationToken cancellationToken)
    {
        var workers = await _workerPool.ListAvailableWorkersAsync(cancellationToken);
        if (workers.Count == 0)
        {
            throw new AgwException(ErrorCodes.JobWorkerUnavailable);
        }

        var worker = SelectWorker(workers);
        return await _workerPool.DispatchAsync(worker, job, cancellationToken);
    }

    private JobWorkerDescriptor SelectWorker(IReadOnlyList<JobWorkerDescriptor> workers)
    {
        var orderedWorkers = workers
            .OrderBy(worker => worker.WorkerId, StringComparer.Ordinal)
            .ToArray();

        var cursor = Interlocked.Increment(ref _workerSelectionCursor);
        var index = (int)(cursor % orderedWorkers.Length);
        if (index < 0)
        {
            index += orderedWorkers.Length;
        }

        return orderedWorkers[index];
    }

    private void ApplyExecutionResult(InMemoryJob current, JobWorkerExecutionResult result)
    {
        if (result.RemoveFromSchedule || !result.NextRunTime.HasValue)
        {
            _taskMap.TryRemove(current.JobId, out _);
            return;
        }

        var updatedJob = InMemoryJobMapper.ToJob(current);
        updatedJob.NextRunTime = result.NextRunTime.Value;
        updatedJob.RetryCount = result.RetryCount;
        UpsertInMemoryTask(updatedJob);
    }

    private void RequeueAfterDispatchFailure(InMemoryJob job)
    {
        var updatedJob = InMemoryJobMapper.ToJob(job);
        updatedJob.NextRunTime = DateTimeOffset.UtcNow.Add(_options.DispatchRetryDelay);
        UpsertInMemoryTask(updatedJob);
    }

    private void ReleaseProjectLane(Guid projectId)
    {
        lock (_queueLock)
        {
            _runningProjects.TryRemove(projectId, out _);
        }
    }

    private void UpsertInMemoryTask(Job job)
    {
        InMemoryJob upserted;

        lock (_queueLock)
        {
            var version = _taskMap.TryGetValue(job.Id, out var existing)
                ? existing.Version + 1
                : 1;

            upserted = InMemoryJobMapper.FromJob(job, version);
            _taskMap[job.Id] = upserted;
            _queue.Enqueue(upserted, upserted.NextRunTime);
        }

        _wakeSignal.Release();
    }

    private bool TryPeekLatest(out InMemoryJob? job)
    {
        lock (_queueLock)
        {
            while (_queue.TryPeek(out var candidate, out _))
            {
                if (_taskMap.TryGetValue(candidate.JobId, out var current) && current.Version == candidate.Version)
                {
                    job = candidate;
                    return true;
                }

                _queue.Dequeue();
            }
        }

        job = null;
        return false;
    }

    private bool TryDequeueLatest(out InMemoryJob? job)
    {
        lock (_queueLock)
        {
            while (_queue.TryDequeue(out var candidate, out _))
            {
                if (_taskMap.TryGetValue(candidate.JobId, out var current) && current.Version == candidate.Version)
                {
                    job = candidate;
                    return true;
                }
            }
        }

        job = null;
        return false;
    }
}
