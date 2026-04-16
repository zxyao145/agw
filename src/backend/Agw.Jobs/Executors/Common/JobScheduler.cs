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

    // SchedulerState is the only synchronization boundary for queue, version, lane, and backlog data.
    private readonly SchedulerState _state = new();

    // Released whenever the dispatch loop should re-check the queue before its current wait expires.
    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    // Released by near-term job creation events so prefetch can pick up new persisted jobs early.
    private readonly SemaphoreSlim _prefetchSignal = new(0, int.MaxValue);

    private long _workerSelectionCursor = -1;

    /// <summary>
    /// Starts the prefetch and dispatch loops until cancellation is requested.
    /// </summary>
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

    /// <summary>
    /// Periodically loads due persisted jobs into the in-memory scheduler state.
    /// </summary>
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

    /// <summary>
    /// Wakes prefetch early when a newly created one-shot job can run soon.
    /// </summary>
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

    /// <summary>
    /// Drives due-job dispatch by repeatedly taking the next state decision.
    /// </summary>
    private async Task RunDispatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var decision = _state.TakeNextDispatch(DateTimeOffset.UtcNow);
                if (decision.ReadyJob is { } job)
                {
                    if (_state.TryClaimProjectLane(job))
                    {
                        _ = RunProjectLaneAsync(job, cancellationToken);
                    }

                    continue;
                }

                await WaitForDispatchWorkAsync(decision.NextRunTime, cancellationToken);
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

    /// <summary>
    /// Waits until there is dispatch work, or until the next known job becomes due.
    /// </summary>
    private async Task WaitForDispatchWorkAsync(DateTimeOffset? nextRunTime, CancellationToken cancellationToken)
    {
        if (!nextRunTime.HasValue)
        {
            await _wakeSignal.WaitAsync(cancellationToken);
            return;
        }

        var delay = nextRunTime.Value - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        // Wait until the next due time, but allow fresh/updated jobs to wake the loop immediately.
        await _wakeSignal.WaitAsync(delay, cancellationToken);
    }

    /// <summary>
    /// Dispatches jobs for one project sequentially until that project's backlog is drained.
    /// </summary>
    private async Task RunProjectLaneAsync(InMemoryJob firstJob, CancellationToken cancellationToken)
    {
        var current = firstJob;
        try
        {
            // Drain this project's backlog in the lane task so same-project dispatch never overlaps.
            while (!cancellationToken.IsCancellationRequested)
            {
                var dispatchResult = await DispatchToSelectedWorkerAsync(current, cancellationToken);
                CompleteDispatchedJob(current, dispatchResult.ExecutionResult);

                if (!_state.TryTakeNextProjectJob(current.ProjectId, out var next) || next == null)
                {
                    return;
                }

                current = next;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Job {JobId} dispatch failed.", current.JobId);
            RequeueAfterDispatchFailure(current);
            _state.ReleaseProjectLane(current.ProjectId);
        }
    }


    #region Dispatch Job To Worker

    /// <summary>
    /// Selects an available worker and dispatches the job to it.
    /// </summary>
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

    /// <summary>
    /// Chooses a worker with a stable round-robin order.
    /// </summary>
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
    #endregion

    #region Finish Job Dispatch

    /// <summary>
    /// Applies the worker result and wakes dispatch if the job was rescheduled.
    /// </summary>
    private void CompleteDispatchedJob(InMemoryJob current, JobWorkerExecutionResult result)
    {
        if (_state.ApplyExecutionResult(current, result))
        {
            _wakeSignal.Release();
        }
    }

    /// <summary>
    /// Requeues a job after worker selection or dispatch failed before execution completed.
    /// </summary>
    private void RequeueAfterDispatchFailure(InMemoryJob job)
    {
        var updatedJob = InMemoryJobMapper.ToJob(job);
        updatedJob.NextRunTime = DateTimeOffset.UtcNow.Add(_options.DispatchRetryDelay);
        UpsertInMemoryTask(updatedJob);
    } 
    #endregion

    /// <summary>
    /// Inserts or updates a job in memory and wakes the dispatch loop.
    /// </summary>
    private void UpsertInMemoryTask(Job job)
    {
        _state.Upsert(job);
        _wakeSignal.Release();
    }
}


/// <summary>
/// Owns all mutable in-memory scheduling state for <see cref="JobScheduler" />.
/// </summary>
/// <remarks>
/// The dispatch loop and project lane tasks can run concurrently, so queue state,
/// latest job versions, active project lanes, and per-project backlogs must be
/// read and updated as one unit. This type is the single synchronization boundary
/// for those structures and keeps stale-entry cleanup, version checks, and lane
/// release rules in one place.
/// </remarks>
internal sealed class SchedulerState
{
    // Protects every field in this type. Callers should never coordinate these structures separately.
    private readonly object _syncRoot = new();

    // PriorityQueue does not support keyed updates, so updated jobs are appended and older versions are skipped.
    private readonly PriorityQueue<InMemoryJob, DateTimeOffset> _queue = new();

    // Latest accepted version per job id. Queue/backlog entries are valid only while their version matches this map.
    private readonly Dictionary<Guid, InMemoryJob> _jobs = new();

    // Project ids currently owned by a lane task.
    private readonly HashSet<Guid> _runningProjects = [];

    // Due jobs waiting behind the active lane for the same project.
    private readonly Dictionary<Guid, Queue<InMemoryJob>> _projectBacklogs = new();

    /// <summary>
    /// Adds or replaces the latest in-memory version of a job.
    /// </summary>
    public void Upsert(Job job)
    {
        lock (_syncRoot)
        {
            UpsertCore(job);
        }
    }

    /// <summary>
    /// Returns the next ready job, next wake time, or empty-wait decision.
    /// </summary>
    public DispatchDecision TakeNextDispatch(DateTimeOffset now)
    {
        lock (_syncRoot)
        {
            // Drop stale queue entries lazily; this keeps updates cheap and local to the state boundary.
            while (_queue.TryPeek(out var candidate, out _))
            {
                if (!IsCurrent(candidate))
                {
                    _queue.Dequeue();
                    continue;
                }

                if (candidate.NextRunTime > now)
                {
                    return DispatchDecision.WaitUntil(candidate.NextRunTime);
                }

                _queue.Dequeue();
                return DispatchDecision.Ready(candidate);
            }

            return DispatchDecision.WaitForWork();
        }
    }

    /// <summary>
    /// Claims the project lane for a job, or appends the job to that project's backlog.
    /// </summary>
    public bool TryClaimProjectLane(InMemoryJob job)
    {
        lock (_syncRoot)
        {
            if (_runningProjects.Add(job.ProjectId))
            {
                return true;
            }

            if (!_projectBacklogs.TryGetValue(job.ProjectId, out var backlog))
            {
                backlog = new Queue<InMemoryJob>();
                _projectBacklogs[job.ProjectId] = backlog;
            }

            backlog.Enqueue(job);
            return false;
        }
    }

    /// <summary>
    /// Gets the next current job from a project's backlog, releasing the lane when none remains.
    /// </summary>
    public bool TryTakeNextProjectJob(Guid projectId, out InMemoryJob? next)
    {
        lock (_syncRoot)
        {
            next = null;
            if (_projectBacklogs.TryGetValue(projectId, out var backlog))
            {
                while (backlog.Count > 0)
                {
                    var candidate = backlog.Dequeue();
                    if (IsCurrent(candidate))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (backlog.Count == 0)
                {
                    _projectBacklogs.Remove(projectId);
                }
            }

            if (next != null)
            {
                return true;
            }

            _runningProjects.Remove(projectId);
            return false;
        }
    }

    /// <summary>
    /// Removes a completed job from memory or reschedules it based on the worker result.
    /// </summary>
    public bool ApplyExecutionResult(InMemoryJob current, JobWorkerExecutionResult result)
    {
        lock (_syncRoot)
        {
            if (result.RemoveFromSchedule || !result.NextRunTime.HasValue)
            {
                _jobs.Remove(current.JobId);
                return false;
            }

            var updatedJob = InMemoryJobMapper.ToJob(current);
            updatedJob.NextRunTime = result.NextRunTime.Value;
            updatedJob.RetryCount = result.RetryCount;
            UpsertCore(updatedJob);
            return true;
        }
    }

    /// <summary>
    /// Releases a project lane after dispatch failed before normal backlog draining.
    /// </summary>
    public void ReleaseProjectLane(Guid projectId)
    {
        lock (_syncRoot)
        {
            _runningProjects.Remove(projectId);
        }
    }

    /// <summary>
    /// Stores a new job version and enqueues it by next run time.
    /// </summary>
    private void UpsertCore(Job job)
    {
        var version = _jobs.TryGetValue(job.Id, out var existing)
            ? existing.Version + 1
            : 1;

        var upserted = InMemoryJobMapper.FromJob(job, version);
        _jobs[job.Id] = upserted;
        _queue.Enqueue(upserted, upserted.NextRunTime);
    }

    /// <summary>
    /// Determines whether a queued or backlogged entry still matches the latest job version.
    /// </summary>
    private bool IsCurrent(InMemoryJob candidate)
    {
        // Stale entries can come from queue updates, retries, or completed jobs that were removed from the schedule.
        return _jobs.TryGetValue(candidate.JobId, out var current)
            && current.Version == candidate.Version;
    }
}

// Represents the dispatch loop's next action without exposing SchedulerState internals.
internal readonly record struct DispatchDecision(InMemoryJob? ReadyJob, DateTimeOffset? NextRunTime)
{
    /// <summary>
    /// Creates a decision to dispatch a job immediately.
    /// </summary>
    public static DispatchDecision Ready(InMemoryJob job)
    {
        return new DispatchDecision(job, null);
    }

    /// <summary>
    /// Creates a decision to wait until the supplied next run time.
    /// </summary>
    public static DispatchDecision WaitUntil(DateTimeOffset nextRunTime)
    {
        return new DispatchDecision(null, nextRunTime);
    }

    /// <summary>
    /// Creates a decision to wait until the scheduler is woken by new work.
    /// </summary>
    public static DispatchDecision WaitForWork()
    {
        return new DispatchDecision(null, null);
    }
}
