using Agw.Domain.Entities;
using Agw.Infrastructure.Data;
using Agw.Jobs.Enums;
using Agw.Jobs.Services;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Services;

public class ScheduledTaskStore(LlmDbContext dbContext) : IScheduledTaskStore
{
    public async Task<IReadOnlyList<ScheduledTask>> PrefetchAsync(DateTimeOffset now, DateTimeOffset horizon, CancellationToken cancellationToken)
    {
        return await dbContext.ScheduledTasks
            .Where(t => t.IsEnabled
                && t.Status == ScheduledTaskStatus.Pending
                && t.NextRunTime <= horizon)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task MarkRunningAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await dbContext.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Scheduled task not found: {taskId}");

        task.Status = ScheduledTaskStatus.Running;
        task.UpdateTime = DateTime.UtcNow;
        task.UpdateBy = "scheduler";

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkSucceededAsync(Guid taskId, DateTimeOffset? nextRunTime, CancellationToken cancellationToken)
    {
        var task = await dbContext.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Scheduled task not found: {taskId}");

        task.RetryCount = 0;
        task.LastError = null;
        task.Status = ScheduledTaskStatus.Pending;

        if (nextRunTime.HasValue)
        {
            task.NextRunTime = nextRunTime.Value;
        }
        else
        {
            task.IsEnabled = false;
            task.Status = ScheduledTaskStatus.Paused;
        }

        task.UpdateTime = DateTime.UtcNow;
        task.UpdateBy = "scheduler";

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkRetryAsync(Guid taskId, DateTimeOffset nextRunTime, int retryCount, string errorMessage, CancellationToken cancellationToken)
    {
        var task = await dbContext.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Scheduled task not found: {taskId}");

        task.Status = ScheduledTaskStatus.Pending;
        task.RetryCount = retryCount;
        task.NextRunTime = nextRunTime;
        task.LastError = errorMessage;
        task.UpdateTime = DateTime.UtcNow;
        task.UpdateBy = "scheduler";

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(Guid taskId, int retryCount, string errorMessage, CancellationToken cancellationToken)
    {
        var task = await dbContext.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Scheduled task not found: {taskId}");

        task.Status = ScheduledTaskStatus.Paused;
        task.IsEnabled = false;
        task.RetryCount = retryCount;
        task.LastError = errorMessage;
        task.UpdateTime = DateTime.UtcNow;
        task.UpdateBy = "scheduler";

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddExecutionLogAsync(Guid taskId, DateTimeOffset startTime, DateTimeOffset endTime, bool success, int attempt, string? errorMessage, CancellationToken cancellationToken)
    {
        var log = new TaskExecutionLog
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            StartTime = startTime,
            EndTime = endTime,
            Success = success,
            Attempt = attempt,
            ErrorMessage = errorMessage,
            CreateBy = "scheduler",
            CreateTime = DateTime.UtcNow,
            UpdateBy = "scheduler",
            UpdateTime = DateTime.UtcNow
        };

        await dbContext.TaskExecutionLogs.AddAsync(log, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
