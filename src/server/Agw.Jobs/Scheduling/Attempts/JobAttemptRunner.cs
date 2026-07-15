using Agw.Jobs.Execution;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.Logging;

namespace Agw.Jobs.Scheduling.Attempts;

/// <summary>
/// Runs one scheduled job attempt, including claim, agent execution, state transition,
/// retry bookkeeping, and execution logging.
/// </summary>
public sealed class JobAttemptRunner
{
    private readonly IJobStore _jobStore;
    private readonly IJobAgentExecutor _jobAgentExecutor;
    private readonly JobScheduleCalculator _scheduleCalculator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JobAttemptRunner> _logger;

    public JobAttemptRunner(
        IJobStore jobStore,
        IJobAgentExecutor jobAgentExecutor,
        JobScheduleCalculator scheduleCalculator,
        TimeProvider timeProvider,
        ILogger<JobAttemptRunner> logger)
    {
        _jobStore = jobStore;
        _jobAgentExecutor = jobAgentExecutor;
        _scheduleCalculator = scheduleCalculator;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<JobAttemptResult> RunAsync(
        ScheduledJob scheduledJob,
        CancellationToken cancellationToken)
    {
        var start = _timeProvider.GetUtcNow();
        var taskId = Guid.Empty;

        try
        {
            var markedRunning = await _jobStore.MarkRunningAsync(
                scheduledJob.JobId,
                cancellationToken);
            if (!markedRunning)
            {
                _logger.LogInformation(
                    "Job {JobId} is no longer enabled/pending. Dropping stale in-memory entry.",
                    scheduledJob.JobId);
                return new JobAttemptResult.Drop();
            }

            var job = ToJob(scheduledJob);
            taskId = await _jobAgentExecutor.ExecuteAsync(job, cancellationToken);

            var nextRunTime = _scheduleCalculator.GetNextRunTime(
                job,
                _timeProvider.GetUtcNow());
            await _jobStore.MarkSucceededAsync(
                scheduledJob.JobId,
                nextRunTime,
                cancellationToken);
            await _jobStore.AddExecutionLogAsync(
                scheduledJob.JobId,
                taskId,
                start,
                _timeProvider.GetUtcNow(),
                success: true,
                attempt: scheduledJob.RetryCount + 1,
                errorMessage: null,
                cancellationToken);

            return nextRunTime.HasValue
                ? new JobAttemptResult.Reschedule(scheduledJob with
                {
                    NextRunTime = nextRunTime.Value,
                    RetryCount = 0
                })
                : new JobAttemptResult.Drop();
        }
        catch (Exception ex)
        {
            if (IsMissingJobException(ex))
            {
                _logger.LogWarning(
                    "Job {JobId} no longer exists. Dropping stale in-memory entry.",
                    scheduledJob.JobId);
                return new JobAttemptResult.Drop();
            }

            _logger.LogError(ex, "Job {JobId} execution failed.", scheduledJob.JobId);
            var retryCount = scheduledJob.RetryCount + 1;

            if (retryCount <= scheduledJob.MaxRetryCount)
            {
                var nextRunTime = _timeProvider.GetUtcNow().Add(JobSchedulingDefaults.RetryDelay);
                try
                {
                    await _jobStore.MarkRetryAsync(
                        scheduledJob.JobId,
                        nextRunTime,
                        retryCount,
                        ex.Message,
                        cancellationToken);
                    await _jobStore.AddExecutionLogAsync(
                        scheduledJob.JobId,
                        taskId,
                        start,
                        _timeProvider.GetUtcNow(),
                        success: false,
                        attempt: retryCount,
                        errorMessage: ex.Message,
                        cancellationToken);
                }
                catch (Exception bookkeepingEx) when (IsMissingJobException(bookkeepingEx))
                {
                    _logger.LogWarning(
                        "Job {JobId} disappeared during retry bookkeeping. Dropping stale in-memory entry.",
                        scheduledJob.JobId);
                    return new JobAttemptResult.Drop();
                }

                return new JobAttemptResult.Reschedule(scheduledJob with
                {
                    NextRunTime = nextRunTime,
                    RetryCount = retryCount
                });
            }

            try
            {
                await _jobStore.MarkFailedAsync(
                    scheduledJob.JobId,
                    retryCount,
                    ex.Message,
                    cancellationToken);
                await _jobStore.AddExecutionLogAsync(
                    scheduledJob.JobId,
                    taskId,
                    start,
                    _timeProvider.GetUtcNow(),
                    success: false,
                    attempt: retryCount,
                    errorMessage: ex.Message,
                    cancellationToken);
            }
            catch (Exception bookkeepingEx) when (IsMissingJobException(bookkeepingEx))
            {
                _logger.LogWarning(
                    "Job {JobId} disappeared during failure bookkeeping. Dropping stale in-memory entry.",
                    scheduledJob.JobId);
            }

            return new JobAttemptResult.Drop();
        }
    }

    private static bool IsMissingJobException(Exception exception)
    {
        return exception is AgwException agwException
            && agwException.Code == ErrorCodes.JobNotFound.Code;
    }

    private static Job ToJob(ScheduledJob scheduledJob)
    {
        return new Job
        {
            Id = scheduledJob.JobId,
            ProjectId = scheduledJob.ProjectId,
            AgentType = scheduledJob.AgentType,
            AgentId = scheduledJob.AgentId,
            Name = scheduledJob.Name,
            Prompt = scheduledJob.Prompt,
            TriggerType = scheduledJob.TriggerType,
            TriggerValue = scheduledJob.TriggerValue,
            NextRunTime = scheduledJob.NextRunTime,
            RetryCount = scheduledJob.RetryCount,
            MaxRetryCount = scheduledJob.MaxRetryCount
        };
    }
}
