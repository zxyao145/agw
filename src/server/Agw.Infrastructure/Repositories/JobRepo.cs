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

    public async Task<IReadOnlyList<Job>> PrefetchAsync(
        DateTimeOffset now,
        DateTimeOffset horizon,
        CancellationToken cancellationToken
    )
    {
        var jobs = await _dbSet.Where(t => t.IsEnabled).AsNoTracking().ToListAsync(cancellationToken);

        return jobs.Where(t => t.Status == JobStatus.Pending && t.NextRunTime <= horizon).ToList();
    }

    public async Task<JobAttemptClaim?> TryStartAttemptAsync(Guid jobId, CancellationToken cancellationToken)
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

            return null;
        }

        if (job.Status != JobStatus.Pending)
        {
            return null;
        }

        var executionId = Guid.CreateVersion7();
        job.Status = JobStatus.Running;
        job.ActiveExecutionId = executionId;
        job.ActiveAttemptStartedAt = now;
        job.UpdateTime = now;
        job.UpdateBy = "scheduler";

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new JobAttemptClaim(job, executionId, now, job.RetryCount + 1);
    }
}
