using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Jobs.Application.Persistence;
using Agw.Jobs.Domain.Behaviors;
using Agw.Jobs.Execution;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Data.Entities.Jobs;
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

    private readonly IJobOutcomeTransaction _transaction;
    private readonly IJobsDbContext _dbContext;
    private readonly IProjectTaskFacade _projectTasks;
    private readonly TimeProvider _timeProvider;

    public JobAttemptOutcomeRecorder(
        IJobsDbContext dbContext,
        IJobOutcomeTransaction transaction,
        IProjectTaskFacade projectTasks,
        TimeProvider timeProvider
    )
    {
        _dbContext = dbContext;
        _transaction = transaction;
        _projectTasks = projectTasks;
        _timeProvider = timeProvider;
    }

    public Task<JobAttemptResult> RecordAsync(
        Guid jobId,
        Guid executionId,
        bool success,
        string? errorMessage,
        CancellationToken cancellationToken
    ) =>
        _transaction.ExecuteAsync(
            jobId,
            token => RecordCoreAsync(jobId, executionId, success, errorMessage, token),
            cancellationToken
        );

    private async Task<JobAttemptResult> RecordCoreAsync(
        Guid jobId,
        Guid executionId,
        bool success,
        string? errorMessage,
        CancellationToken cancellationToken
    )
    {
        Job? job;
        using (UserInfoUtil.PushSystemScope())
        {
            // Reload the identity-map entry under the outcome gate before checking idempotency.
            var tracked = _dbContext.Jobs.Local.FirstOrDefault(item => item.Id == jobId);
            if (tracked != null)
                await _dbContext.Jobs.Entry(tracked).ReloadAsync(cancellationToken);
            job = await _dbContext
                .Jobs.SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
                .ConfigureAwait(false);
        }
        if (job == null || !new JobBehavior(job).IsActiveAttempt(executionId))
        {
            return new JobAttemptResult.Drop();
        }

        var normalizedError = success ? null : errorMessage ?? "The Job attempt failed.";
        var startedAt = job.ActiveAttemptStartedAt.GetValueOrDefault();
        if (!JobAgentExecutor.TryResolveOwnerUserId(job, out var ownerUserId))
        {
            return await RecordMissingOwnerAsync(job, executionId, startedAt, cancellationToken).ConfigureAwait(false);
        }

        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerUserId)], "ScheduledJob"))
        );
        if (success)
        {
            _ = await _projectTasks
                .FinishAsync(
                    new FinishProjectTaskRequest(executionId, ProjectTaskStatus.Succeeded, null, ownerUserId),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else
        {
            _ = await _projectTasks
                .FinishAsync(
                    new FinishProjectTaskRequest(executionId, ProjectTaskStatus.Failed, normalizedError, ownerUserId),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow();
        var behavior = new JobBehavior(job);
        var attempt = behavior.GetCurrentAttempt();
        var rescheduled = success
            ? behavior.TryRescheduleAfterSuccess(now)
            : behavior.TryRescheduleAfterFailure(normalizedError!, now.Add(JobSchedulingDefaults.RetryDelay));
        job.UpdateBy = SchedulerUser;
        job.UpdateTime = now;
        await _dbContext.JobLogs.AddAsync(
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
            },
            cancellationToken
        );
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return rescheduled ? new JobAttemptResult.Reschedule(ScheduledJob.FromJob(job)) : new JobAttemptResult.Drop();
    }

    private async Task<JobAttemptResult> RecordMissingOwnerAsync(
        Job job,
        Guid executionId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken
    )
    {
        var now = _timeProvider.GetUtcNow();
        var behavior = new JobBehavior(job);
        var attempt = behavior.GetCurrentAttempt();
        using var systemScope = UserInfoUtil.PushSystemScope();

        behavior.PauseForMissingOwner();
        job.UpdateBy = SchedulerUser;
        job.UpdateTime = now;
        await _dbContext.JobLogs.AddAsync(
            new JobLog
            {
                Id = Guid.CreateVersion7(),
                JobId = job.Id,
                TaskId = executionId,
                StartTime = startedAt,
                EndTime = now,
                Success = false,
                Attempt = attempt,
                ErrorMessage = job.LastError,
                CreateBy = SchedulerUser,
                CreateTime = now,
                UpdateBy = SchedulerUser,
                UpdateTime = now,
            },
            cancellationToken
        );
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new JobAttemptResult.Drop();
    }
}
