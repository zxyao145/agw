using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Attempts;
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

    public async Task<IReadOnlyList<Job>> ListRunningAsync(CancellationToken cancellationToken) =>
        await _dbSet
            .IgnoreUserScope()
            .AsNoTracking()
            .Where(job => job.Status == JobStatus.Running)
            .ToListAsync(cancellationToken);

    public Task<Job?> GetRunningAsync(Guid jobId, CancellationToken cancellationToken) =>
        _dbSet
            .IgnoreUserScope()
            .FirstOrDefaultAsync(job => job.Id == jobId && job.Status == JobStatus.Running, cancellationToken);

    public async Task<IReadOnlyList<Job>> PrefetchAsync(
        DateTimeOffset now,
        DateTimeOffset horizon,
        CancellationToken cancellationToken
    )
    {
        var jobs = await _dbSet.IgnoreUserScope().Where(t => t.IsEnabled).AsNoTracking().ToListAsync(cancellationToken);

        return jobs.Where(t => t.Status == JobStatus.Pending && t.NextRunTime <= horizon).ToList();
    }

    public async Task<JobAttemptStartResult> TryStartAttemptAsync(
        ScheduledJob scheduledJob,
        CancellationToken cancellationToken
    )
    {
        var now = _timeProvider.GetUtcNow();
        var job = await _dbSet
            .IgnoreUserScope()
            .FirstOrDefaultAsync(t => t.Id == scheduledJob.JobId && t.IsEnabled, cancellationToken);

        if (job == null)
        {
            var exists = await _dbSet
                .IgnoreUserScope()
                .AsNoTracking()
                .AnyAsync(t => t.Id == scheduledJob.JobId, cancellationToken);

            if (!exists)
            {
                throw new AgwException(ErrorCodes.JobNotFound, $"Job not found: {scheduledJob.JobId}");
            }

            return new JobAttemptStartResult.Drop();
        }

        if (job.Status != JobStatus.Pending)
        {
            return new JobAttemptStartResult.Drop();
        }

        if (job.ProjectId != scheduledJob.ProjectId || job.NextRunTime > now)
        {
            return new JobAttemptStartResult.Reschedule(ScheduledJob.FromJob(job));
        }

        if (string.IsNullOrWhiteSpace(job.CreateBy))
        {
            job.Status = JobStatus.Paused;
            job.IsEnabled = false;
            job.LastError = "The Job owner is missing.";
            job.ActiveTurnId = null;
            job.ActiveAttemptStartedAt = null;
            job.UpdateTime = now;
            job.UpdateBy = "scheduler";
            using (UserInfoUtil.PushSystemScope())
            {
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return new JobAttemptStartResult.Drop();
        }

        var turnId = Guid.CreateVersion7();
        job.Status = JobStatus.Running;
        job.ActiveTurnId = turnId;
        job.ActiveAttemptStartedAt = now;
        job.UpdateTime = now;
        job.UpdateBy = "scheduler";

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new JobAttemptStartResult.Claimed(new JobAttemptClaim(job, turnId, now, job.RetryCount + 1));
    }
}
