using System.Text.Json;
using DSystem.Domain.Entities;
using DSystem.Domain.Enums;
using DSystem.Domain.Services;
using DSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProjectTaskSchedulerHostedService started.");

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
        await semaphore.WaitAsync(stoppingToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var dbContext = scope.ServiceProvider.GetRequiredService<LlmDbContext>();
            var taskService = scope.ServiceProvider.GetRequiredService<ProjectTaskDomainService>();
            var workflowRuntime = scope.ServiceProvider.GetRequiredService<WorkflowRuntimeService>();

            var leaseAcquired = await TryAcquireProjectLeaseAsync(dbContext, projectId, stoppingToken);
            if (!leaseAcquired)
            {
                return;
            }

            try
            {
                // If there is a running task, we do not start another one for this project.
                var running = await taskService.ListAsync(t => t.ProjectId == projectId && t.Status == ProjectTaskStatus.Running);
                if (running.Count > 0)
                {
                    return;
                }

                var next = await taskService.GetNextPendingAsync(projectId);
                if (next == null)
                {
                    return;
                }

                var marked = await taskService.TryMarkRunningAsync(next.Id, user: "scheduler");
                if (marked == null)
                {
                    return;
                }

                // Extend lease while executing.
                await RenewProjectLeaseAsync(dbContext, projectId, stoppingToken);

                try
                {
                    var execution = await workflowRuntime.ExecuteAsync(marked.WorkflowId, marked.Input, stoppingToken);
                    if (execution == null)
                    {
                        await taskService.MarkFailedAsync(marked.Id, "Workflow execution failed (workflow disabled/missing or agent runtime unavailable).", "scheduler");
                        return;
                    }

                    var json = JsonSerializer.Serialize(execution, new JsonSerializerOptions { WriteIndented = false });
                    await taskService.MarkSucceededAsync(marked.Id, json, "scheduler");
                }
                catch (Exception ex)
                {
                    await taskService.MarkFailedAsync(marked.Id, ex.Message, "scheduler");
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

        // 1) Try insert (fast path for first time).
        try
        {
            await dbContext.ProjectLeases.AddAsync(new ProjectLease
            {
                ProjectId = projectId,
                LockedBy = _instanceId,
                LockedUntilUtc = until,
                CreateBy = _instanceId,
                CreateTime = now
            }, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Another instance already created the lease row. We'll try to claim it if expired.
            dbContext.ChangeTracker.Clear();
        }

        // 2) Try claim if expired OR renew if we already own it.
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE ProjectLeases
SET LockedBy = {_instanceId},
    LockedUntilUtc = {until},
    UpdateBy = {_instanceId},
    UpdateTime = {now}
WHERE ProjectId = {projectId}
  AND (LockedUntilUtc <= {now} OR LockedBy = {_instanceId})
", cancellationToken);

        return affected == 1;
    }

    private Task RenewProjectLeaseAsync(LlmDbContext dbContext, Guid projectId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(_projectLeaseDuration);

        return dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE ProjectLeases
SET LockedUntilUtc = {until},
    UpdateBy = {_instanceId},
    UpdateTime = {now}
WHERE ProjectId = {projectId}
  AND LockedBy = {_instanceId}
", cancellationToken);
    }

    private Task ReleaseProjectLeaseAsync(LlmDbContext dbContext, Guid projectId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        return dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE ProjectLeases
SET LockedUntilUtc = {now},
    UpdateBy = {_instanceId},
    UpdateTime = {now}
WHERE ProjectId = {projectId}
  AND LockedBy = {_instanceId}
", cancellationToken);
    }
}