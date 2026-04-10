using Agw.Infrastructure.Data;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Domain.Entities;
using Agw.Jobs.Domain.Enums;

using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Services;

public class JobStore(AgwDbContext dbContext) : IJobStore
{
    public async Task<IReadOnlyList<Job>> PrefetchAsync(DateTimeOffset now, DateTimeOffset horizon, CancellationToken cancellationToken)
    {
        var jobs = await dbContext.Jobs
            .Where(t => t.IsEnabled)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return jobs
            .Where(t => t.Status == JobStatus.Pending
                && t.NextRunTime <= horizon)
            .ToList();
    }

    public async Task<bool> MarkRunningAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var job = await dbContext.Jobs
            .FirstOrDefaultAsync(t => t.Id == jobId && t.IsEnabled, cancellationToken);

        if (job == null)
        {
            var exists = await dbContext.Jobs
                .AsNoTracking()
                .AnyAsync(t => t.Id == jobId, cancellationToken);

            if (!exists)
            {
                throw new InvalidOperationException($"Job not found: {jobId}");
            }

            return false;
        }

        if (job.Status != JobStatus.Pending)
        {
            return false;
        }

        job.Status = JobStatus.Running;
        job.UpdateTime = now;
        job.UpdateBy = "scheduler";

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
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

    public async Task AddExecutionLogAsync(Guid jobId, Guid taskId, DateTimeOffset startTime, DateTimeOffset endTime, bool success, int attempt, string? errorMessage, CancellationToken cancellationToken)
    {
        var log = new JobLog
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
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

        await dbContext.JobLogs.AddAsync(log, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
