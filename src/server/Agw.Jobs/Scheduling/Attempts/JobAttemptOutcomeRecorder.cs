using Agw.Jobs.Execution;
using Agw.Projects.Application;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Scheduling.Attempts;

public interface IJobAttemptOutcomeRecorder
{
    Task<JobAttemptResult> RecordAsync(
        Guid jobId,
        Guid executionId,
        bool success,
        string? errorMessage,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Applies one durable or in-process attempt outcome to the project Task, Job, and JobLog.
/// </summary>
public sealed class JobAttemptOutcomeRecorder : IJobAttemptOutcomeRecorder
{
    private const string SchedulerUser = "scheduler";

    private readonly IRepository<Job> _jobRepository;
    private readonly IRepository<JobLog> _jobLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TaskExecutionAppService _taskExecutionAppService;
    private readonly JobScheduleCalculator _scheduleCalculator;
    private readonly TimeProvider _timeProvider;

    public JobAttemptOutcomeRecorder(
        IRepository<Job> jobRepository,
        IRepository<JobLog> jobLogRepository,
        IUnitOfWork unitOfWork,
        TaskExecutionAppService taskExecutionAppService,
        JobScheduleCalculator scheduleCalculator,
        TimeProvider timeProvider
    )
    {
        _jobRepository = jobRepository;
        _jobLogRepository = jobLogRepository;
        _unitOfWork = unitOfWork;
        _taskExecutionAppService = taskExecutionAppService;
        _scheduleCalculator = scheduleCalculator;
        _timeProvider = timeProvider;
    }

    public async Task<JobAttemptResult> RecordAsync(
        Guid jobId,
        Guid executionId,
        bool success,
        string? errorMessage,
        CancellationToken cancellationToken
    )
    {
        var job = await _jobRepository
            .Queryable.SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
            .ConfigureAwait(false);
        if (
            job == null
            || job.Status != JobStatus.Running
            || job.ActiveExecutionId != executionId
            || !job.ActiveAttemptStartedAt.HasValue
        )
        {
            return new JobAttemptResult.Drop();
        }

        var normalizedError = success ? null : errorMessage ?? "The Job attempt failed.";
        var startedAt = job.ActiveAttemptStartedAt.Value;
        var ownerUserId = JobAgentExecutor.ResolveOwnerUserId(job);
        if (success)
        {
            _ = await _taskExecutionAppService.MarkSucceededAsync(executionId, ownerUserId).ConfigureAwait(false);
        }
        else
        {
            _ = await _taskExecutionAppService
                .MarkFailedAsync(executionId, normalizedError!, ownerUserId)
                .ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow();
        var attempt = job.RetryCount + 1;
        JobAttemptResult result;
        if (success)
        {
            var nextRunTime = job.IsEnabled ? _scheduleCalculator.GetNextRunTime(job, now) : null;
            job.RetryCount = 0;
            job.LastError = null;
            if (nextRunTime.HasValue)
            {
                job.Status = JobStatus.Pending;
                job.NextRunTime = nextRunTime.Value;
                result = new JobAttemptResult.Reschedule(ScheduledJob.FromJob(job));
            }
            else
            {
                job.Status = JobStatus.Paused;
                job.IsEnabled = false;
                result = new JobAttemptResult.Drop();
            }
        }
        else
        {
            var retryCount = attempt;
            job.RetryCount = retryCount;
            job.LastError = normalizedError;
            if (job.IsEnabled && retryCount <= job.MaxRetryCount)
            {
                job.Status = JobStatus.Pending;
                job.NextRunTime = now.Add(JobSchedulingDefaults.RetryDelay);
                result = new JobAttemptResult.Reschedule(ScheduledJob.FromJob(job));
            }
            else
            {
                job.Status = JobStatus.Paused;
                job.IsEnabled = false;
                result = new JobAttemptResult.Drop();
            }
        }

        job.ActiveExecutionId = null;
        job.ActiveAttemptStartedAt = null;
        job.UpdateBy = SchedulerUser;
        job.UpdateTime = now;
        _jobRepository.Update(job);

        await _jobLogRepository.AddAsync(
            new JobLog
            {
                Id = Guid.CreateVersion7(),
                JobId = job.Id,
                TaskId = executionId,
                StartTime = startedAt,
                EndTime = now,
                Success = success,
                Attempt = attempt,
                ErrorMessage = normalizedError,
                CreateBy = SchedulerUser,
                CreateTime = now,
                UpdateBy = SchedulerUser,
                UpdateTime = now,
            }
        );
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return result is JobAttemptResult.Reschedule
            ? new JobAttemptResult.Reschedule(ScheduledJob.FromJob(job))
            : result;
    }
}
