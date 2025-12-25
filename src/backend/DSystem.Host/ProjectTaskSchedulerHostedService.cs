using DSystem.Domain.Entities;
using DSystem.Domain.Enums;
using DSystem.Domain.Services;
using DSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace DSystem.Host;

/// <summary>
/// Background scheduler that executes project tasks.
/// Rules:
/// - Tasks are executed sequentially within a project.
/// - Projects can execute in parallel (bounded by concurrency).
/// - Pending task order is determined by UpdateTime (FIFO). Pending tasks can be reordered by updating UpdateTime.
///
/// NOTE (multi-instance):
/// In-process primitives like SemaphoreSlim do NOT prevent duplicate execution across instances.
/// We use a DB-backed project lease (<see cref="ProjectLease"/>) as a distributed lock to ensure only one instance
/// schedules tasks for a project at a time.
/// </summary>
public class ProjectTaskSchedulerHostedService : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("DSystem.ProjectTaskScheduler");
    private static readonly Meter Meter = new("DSystem.ProjectTaskScheduler");

    private readonly Counter<long> _tasksExecutedCounter;
    private readonly Counter<long> _tasksFailedCounter;
    private readonly Counter<long> _leaseAcquiredCounter;
    private readonly Counter<long> _leaseFailedCounter;
    private readonly Histogram<double> _taskExecutionDuration;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProjectTaskSchedulerHostedService> _logger;

    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private readonly int _maxProjectConcurrency = 4;

    private readonly TimeSpan _projectLeaseDuration = TimeSpan.FromSeconds(30);
    private readonly string _instanceId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public ProjectTaskSchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ProjectTaskSchedulerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        // Initialize metrics
        _tasksExecutedCounter = Meter.CreateCounter<long>(
            "dsystem.tasks.executed",
            description: "Number of tasks successfully executed");
        _tasksFailedCounter = Meter.CreateCounter<long>(
            "dsystem.tasks.failed",
            description: "Number of tasks that failed");
        _leaseAcquiredCounter = Meter.CreateCounter<long>(
            "dsystem.leases.acquired",
            description: "Number of project leases successfully acquired");
        _leaseFailedCounter = Meter.CreateCounter<long>(
            "dsystem.leases.failed",
            description: "Number of project lease acquisition failures");
        _taskExecutionDuration = Meter.CreateHistogram<double>(
            "dsystem.tasks.duration",
            unit: "ms",
            description: "Task execution duration in milliseconds");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProjectTaskSchedulerHostedService started.");

        // 控制单个实例 project 的并发数量
        var semaphore = new SemaphoreSlim(_maxProjectConcurrency, _maxProjectConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var projectService = scope.ServiceProvider.GetRequiredService<ProjectDomainService>();

                var projects = await projectService.ListAsync(p => p.Enable);
                var tasks = projects.Select(p => RunProjectOnceAsync(p.Id, semaphore, stoppingToken)).ToList();

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler loop failed.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }

        _logger.LogInformation("ProjectTaskSchedulerHostedService stopped.");
    }

    private async Task RunProjectOnceAsync(Guid projectId, SemaphoreSlim semaphore, CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("RunProjectOnce", ActivityKind.Internal);
        activity?.SetTag("project.id", projectId);
        activity?.SetTag("instance.id", _instanceId);

        await semaphore.WaitAsync(stoppingToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var dbContext = scope.ServiceProvider.GetRequiredService<LlmDbContext>();
            var taskService = scope.ServiceProvider.GetRequiredService<ProjectTaskDomainService>();
            var agentflowRuntime = scope.ServiceProvider.GetRequiredService<AgentflowRuntimeService>();

            // 锁定项目 projectId
            var leaseAcquired = await TryAcquireProjectLeaseAsync(dbContext, projectId, stoppingToken);
            if (!leaseAcquired)
            {
                _leaseFailedCounter.Add(1, new KeyValuePair<string, object?>("project.id", projectId));
                activity?.SetTag("lease.acquired", false);
                return;
            }

            _leaseAcquiredCounter.Add(1, new KeyValuePair<string, object?>("project.id", projectId));
            activity?.SetTag("lease.acquired", true);

            try
            {
                // If there is a running task, we do not start another one for this project.
                var running = await taskService.ListAsync(t => t.ProjectId == projectId && t.Status == ProjectTaskStatus.Running);
                if (running.Count > 0)
                {
                    activity?.SetTag("skip.reason", "task_already_running");
                    return;
                }

                var next = await taskService.GetNextPendingAsync(projectId);
                if (next == null)
                {
                    activity?.SetTag("skip.reason", "no_pending_tasks");
                    return;
                }

                activity?.SetTag("task.id", next.Id);
                activity?.SetTag("agentflow.id", next.AgentflowId);

                var marked = await taskService.TryMarkRunningAsync(next.Id, user: "scheduler");
                if (marked == null)
                {
                    activity?.SetTag("skip.reason", "mark_running_failed");
                    return;
                }

                // Extend lease while executing.
                await RenewProjectLeaseAsync(dbContext, projectId, stoppingToken);

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    using var taskActivity = ActivitySource.StartActivity("ExecuteAgentflow", ActivityKind.Internal);
                    taskActivity?.SetTag("task.id", marked.Id);
                    taskActivity?.SetTag("agentflow.id", marked.AgentflowId);

                    var execution = await agentflowRuntime.ExecuteAsync(marked.AgentflowId, marked.Input, stoppingToken);
                    stopwatch.Stop();

                    if (execution == null)
                    {
                        await taskService.MarkFailedAsync(marked.Id, "Agentflow execution failed (agentflow disabled/missing or agent runtime unavailable).", "scheduler");
                        _tasksFailedCounter.Add(1,
                            new KeyValuePair<string, object?>("task.id", marked.Id),
                            new KeyValuePair<string, object?>("agentflow.id", marked.AgentflowId),
                            new KeyValuePair<string, object?>("reason", "agentflow_unavailable"));
                        taskActivity?.SetStatus(ActivityStatusCode.Error, "Agentflow unavailable");
                        return;
                    }

                    var options1 = new JsonSerializerOptions
                    {
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
                        WriteIndented = false
                    };
                    var json = JsonSerializer.Serialize(execution, options1);
                    await taskService.MarkSucceededAsync(marked.Id, json, "scheduler");

                    _tasksExecutedCounter.Add(1,
                        new KeyValuePair<string, object?>("task.id", marked.Id),
                        new KeyValuePair<string, object?>("agentflow.id", marked.AgentflowId));
                    _taskExecutionDuration.Record(stopwatch.ElapsedMilliseconds,
                        new KeyValuePair<string, object?>("task.id", marked.Id),
                        new KeyValuePair<string, object?>("agentflow.id", marked.AgentflowId),
                        new KeyValuePair<string, object?>("status", "success"));
                    taskActivity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    await taskService.MarkFailedAsync(marked.Id, ex.Message, "scheduler");
                    _tasksFailedCounter.Add(1,
                        new KeyValuePair<string, object?>("task.id", marked.Id),
                        new KeyValuePair<string, object?>("agentflow.id", marked.AgentflowId),
                        new KeyValuePair<string, object?>("reason", "execution_exception"));
                    _taskExecutionDuration.Record(stopwatch.ElapsedMilliseconds,
                        new KeyValuePair<string, object?>("task.id", marked.Id),
                        new KeyValuePair<string, object?>("agentflow.id", marked.AgentflowId),
                        new KeyValuePair<string, object?>("status", "failed"));
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                }
            }
            finally
            {
                await ReleaseProjectLeaseAsync(dbContext, projectId, stoppingToken);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<bool> TryAcquireProjectLeaseAsync(LlmDbContext dbContext, Guid projectId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(_projectLeaseDuration);

        // 1) Try update first (common case: lease already exists).
        // Claim if expired OR renew if we already own it.
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE project_leases
SET locked_by = {_instanceId},
    locked_until_utc = {until},
    update_by = {_instanceId},
    update_time = {now}
WHERE project_id = {projectId}
  AND (locked_until_utc <= {now} OR locked_by = {_instanceId})
", cancellationToken);

        if (affected == 1)
        {
            return true;
        }

        // 2) If update failed (lease doesn't exist or held by another instance), try insert.
        try
        {
            await dbContext.ProjectLeases.AddAsync(new ProjectLease
            {
                ProjectId = projectId,
                LockedBy = _instanceId,
                LockedUntilUtc = until,
                CreateBy = _instanceId,
                CreateTime = now,
                UpdateBy = _instanceId,
                UpdateTime = now
            }, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Insert failed - another instance created the lease between step 1 and 2.
            // This is rare but possible in multi-instance environments.
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    private Task RenewProjectLeaseAsync(LlmDbContext dbContext, Guid projectId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(_projectLeaseDuration);

        return dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE project_leases
SET locked_until_utc = {until},
    update_by = {_instanceId},
    update_time = {now}
WHERE project_id = {projectId}
  AND locked_by = {_instanceId}
", cancellationToken);
    }

    private Task ReleaseProjectLeaseAsync(LlmDbContext dbContext, Guid projectId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        return dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE project_leases
SET locked_until_utc = {now},
    update_by = {_instanceId},
    update_time = {now}
WHERE project_id = {projectId}
  AND locked_by = {_instanceId}
", cancellationToken);
    }
}