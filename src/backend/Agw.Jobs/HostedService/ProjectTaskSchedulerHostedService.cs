using Agw.Domain.Entities;
using Agw.Jobs.Services;
using Agw.Shared;
using Agw.Shared.Enums;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Agw.Tasks.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agw.Jobs.HostedService;

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
    private static readonly ActivitySource ActivitySource = new("Agw.ProjectTaskScheduler");
    private static readonly Meter Meter = new("Agw.ProjectTaskScheduler");

    private readonly Counter<long> _tasksExecutedCounter;
    private readonly Counter<long> _tasksFailedCounter;
    private readonly Counter<long> _leaseAcquiredCounter;
    private readonly Counter<long> _leaseFailedCounter;
    private readonly Histogram<double> _taskExecutionDuration;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProjectTaskSchedulerHostedService> _logger;

    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private readonly int _maxProjectConcurrency = 4;

    private readonly string _instanceId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public ProjectTaskSchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ProjectTaskSchedulerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        // Initialize metrics
        _tasksExecutedCounter = Meter.CreateCounter<long>(
            "agw.tasks.executed",
            description: "Number of tasks successfully executed");
        _tasksFailedCounter = Meter.CreateCounter<long>(
            "agw.tasks.failed",
            description: "Number of tasks that failed");
        _leaseAcquiredCounter = Meter.CreateCounter<long>(
            "agw.leases.acquired",
            description: "Number of project leases successfully acquired");
        _leaseFailedCounter = Meter.CreateCounter<long>(
            "agw.leases.failed",
            description: "Number of project lease acquisition failures");
        _taskExecutionDuration = Meter.CreateHistogram<double>(
            "agw.tasks.duration",
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
                var projectService = scope.ServiceProvider.GetRequiredService<IProjectAppService>();

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

            var taskService = scope.ServiceProvider.GetRequiredService<ProjectTaskAppService>();
            var projectLeaseService = scope.ServiceProvider.GetRequiredService<IProjectLeaseService>();

            // 锁定项目 projectId
            var leaseAcquired = await projectLeaseService.TryAcquireAsync(projectId, _instanceId, stoppingToken);
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
                if (await taskService.HasRunningTaskAsync(projectId))
                {
                    activity?.SetTag("skip.reason", "task_already_running");
                    return;
                }

                var nextTask = (await taskService.ListAsync(task =>
                        task.ProjectId == projectId && task.Status == ProjectTaskStatus.Pending))
                    .OrderBy(task => task.UpdateTime ?? task.CreateTime)
                    .ThenBy(task => task.CreateTime)
                    .FirstOrDefault();
                if (nextTask == null)
                {
                    activity?.SetTag("skip.reason", "no_pending_tasks");
                    return;
                }

                activity?.SetTag("task.id", nextTask.Id);
                activity?.SetTag("task.context_id", nextTask.ContextId);
                activity?.SetTag("skip.reason", "session_tasks_not_schedulable");
                _logger.LogInformation(
                    "Skipping pending project task {TaskId} in project {ProjectId} because session-based tasks no longer carry execution targets.",
                    nextTask.Id,
                    projectId);
            }
            finally
            {
                await projectLeaseService.ReleaseAsync(projectId, _instanceId, stoppingToken);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

}
