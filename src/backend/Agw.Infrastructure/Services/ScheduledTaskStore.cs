using Agw.Domain.Entities;
using Agw.Infrastructure.Data;
using Agw.Jobs.Enums;
using Agw.Jobs.Services;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Services;

public class JobStore(LlmDbContext dbContext) : IJobStore
{
    public async Task<IReadOnlyList<Job>> PrefetchAsync(DateTimeOffset now, DateTimeOffset horizon, CancellationToken cancellationToken)
    {
        return await dbContext.Jobs
            .Where(t => t.IsEnabled
                && t.Status == JobStatus.Pending
                && t.NextRunTime <= horizon)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> MarkRunningAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var rowsAffected = await dbContext.Jobs
            .Where(t => t.Id == jobId
                && t.IsEnabled
                && t.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Status, JobStatus.Running)
                    .SetProperty(t => t.UpdateTime, now)
                    .SetProperty(t => t.UpdateBy, "scheduler"),
                cancellationToken);

        if (rowsAffected > 0)
        {
            return true;
        }

        var exists = await dbContext.Jobs
            .AsNoTracking()
            .AnyAsync(t => t.Id == jobId, cancellationToken);

        if (!exists)
        {
            throw new InvalidOperationException($"Job not found: {jobId}");
        }

        return false;
    }

    public async Task MarkSucceededAsync(Guid jobId, DateTimeOffset? nextRunTime, CancellationToken cancellationToken)
    {
        var task = await dbContext.Jobs.FirstOrDefaultAsync(t => t.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job not found: {jobId}");

        task.RetryCount = 0;
        task.LastError = null;
        task.Status = JobStatus.Pending;

        if (nextRunTime.HasValue)
        {
            task.NextRunTime = nextRunTime.Value;
        }
        else
        {
            task.IsEnabled = false;
            task.Status = JobStatus.Paused;
        }

        task.UpdateTime = DateTime.UtcNow;
        task.UpdateBy = "scheduler";

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkRetryAsync(Guid jobId, DateTimeOffset nextRunTime, int retryCount, string errorMessage, CancellationToken cancellationToken)
    {
        var task = await dbContext.Jobs.FirstOrDefaultAsync(t => t.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job not found: {jobId}");

        task.Status = JobStatus.Pending;
        task.RetryCount = retryCount;
        task.NextRunTime = nextRunTime;
        task.LastError = errorMessage;
        task.UpdateTime = DateTime.UtcNow;
        task.UpdateBy = "scheduler";

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(Guid jobId, int retryCount, string errorMessage, CancellationToken cancellationToken)
    {
        var task = await dbContext.Jobs.FirstOrDefaultAsync(t => t.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job not found: {jobId}");

        task.Status = JobStatus.Paused;
        task.IsEnabled = false;
        task.RetryCount = retryCount;
        task.LastError = errorMessage;
        task.UpdateTime = DateTime.UtcNow;
        task.UpdateBy = "scheduler";

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddExecutionLogAsync(Guid taskId, DateTimeOffset startTime, DateTimeOffset endTime, bool success, int attempt, string? errorMessage, CancellationToken cancellationToken)
    {
        var log = new JobLog
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
