using Agw.Jobs.Scheduling;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Repositories;

public class JobRepo : EfRepository<Job>, IRepository<Job>, IJobStore
{
    private readonly TimeProvider _timeProvider;

    public JobRepo(DbContext dbContext, TimeProvider timeProvider)
        : base(dbContext)
    {
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<Job>> PrefetchAsync(
        DateTimeOffset now,
        DateTimeOffset horizon,
        CancellationToken cancellationToken
    )
    {
        var jobs = await _dbSet.Where(t => t.IsEnabled).AsNoTracking().ToListAsync(cancellationToken);

        return jobs.Where(t => t.Status == JobStatus.Pending && t.NextRunTime <= horizon).ToList();
    }

    public async Task<bool> MarkRunningAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var job = await _dbSet.FirstOrDefaultAsync(t => t.Id == jobId && t.IsEnabled, cancellationToken);

        if (job == null)
        {
            var exists = await _dbSet.AsNoTracking().AnyAsync(t => t.Id == jobId, cancellationToken);

            if (!exists)
            {
                throw new AgwException(ErrorCodes.JobNotFound, $"Job not found: {jobId}");
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

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task MarkSucceededAsync(Guid jobId, DateTimeOffset? nextRunTime, CancellationToken cancellationToken)
    {
        var task =
            await _dbSet.FirstOrDefaultAsync(t => t.Id == jobId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.JobNotFound, $"Job not found: {jobId}");

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

        task.UpdateTime = _timeProvider.GetUtcNow();
        task.UpdateBy = "scheduler";

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkRetryAsync(
        Guid jobId,
        DateTimeOffset nextRunTime,
        int retryCount,
        string errorMessage,
        CancellationToken cancellationToken
    )
    {
        var task =
            await _dbSet.FirstOrDefaultAsync(t => t.Id == jobId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.JobNotFound, $"Job not found: {jobId}");

        task.Status = JobStatus.Pending;
        task.RetryCount = retryCount;
        task.NextRunTime = nextRunTime;
        task.LastError = errorMessage;
        task.UpdateTime = _timeProvider.GetUtcNow();
        task.UpdateBy = "scheduler";

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid jobId,
        int retryCount,
        string errorMessage,
        CancellationToken cancellationToken
    )
    {
        var task =
            await _dbSet.FirstOrDefaultAsync(t => t.Id == jobId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.JobNotFound, $"Job not found: {jobId}");

        task.Status = JobStatus.Paused;
        task.IsEnabled = false;
        task.RetryCount = retryCount;
        task.LastError = errorMessage;
        task.UpdateTime = _timeProvider.GetUtcNow();
        task.UpdateBy = "scheduler";

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddExecutionLogAsync(
        Guid jobId,
        Guid taskId,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        bool success,
        int attempt,
        string? errorMessage,
        CancellationToken cancellationToken
    )
    {
        var now = _timeProvider.GetUtcNow();
        var log = new JobLog
        {
            Id = Guid.CreateVersion7(),
            JobId = jobId,
            TaskId = taskId,
            StartTime = startTime,
            EndTime = endTime,
            Success = success,
            Attempt = attempt,
            ErrorMessage = errorMessage,
            CreateBy = "scheduler",
            CreateTime = now,
            UpdateBy = "scheduler",
            UpdateTime = now,
        };

        await _dbContext.Set<JobLog>().AddAsync(log, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
