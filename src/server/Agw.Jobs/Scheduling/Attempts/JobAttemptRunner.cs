using Agw.Jobs.Execution;
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
    private readonly IJobAttemptOutcomeRecorder _outcomeRecorder;
    private readonly ILogger<JobAttemptRunner> _logger;

    public JobAttemptRunner(
        IJobStore jobStore,
        IJobAgentExecutor jobAgentExecutor,
        IJobAttemptOutcomeRecorder outcomeRecorder,
        ILogger<JobAttemptRunner> logger
    )
    {
        _jobStore = jobStore;
        _jobAgentExecutor = jobAgentExecutor;
        _outcomeRecorder = outcomeRecorder;
        _logger = logger;
    }

    public async Task<JobAttemptResult> RunAsync(ScheduledJob scheduledJob, CancellationToken cancellationToken)
    {
        JobAttemptClaim? claim;
        try
        {
            claim = await _jobStore.TryStartAttemptAsync(scheduledJob.JobId, cancellationToken);
            if (claim == null)
            {
                _logger.LogInformation(
                    "Job {JobId} is no longer enabled/pending. Dropping stale in-memory entry.",
                    scheduledJob.JobId
                );
                return new JobAttemptResult.Drop();
            }
        }
        catch (Exception ex)
        {
            if (IsMissingJobException(ex))
            {
                _logger.LogWarning("Job {JobId} no longer exists. Dropping stale in-memory entry.", scheduledJob.JobId);
                return new JobAttemptResult.Drop();
            }

            _logger.LogError(ex, "Failed to claim Job {JobId}.", scheduledJob.JobId);
            throw;
        }

        try
        {
            await _jobAgentExecutor.ExecuteAsync(claim.Job, claim.ExecutionId, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Job {JobId} execution failed.", scheduledJob.JobId);
            return await _outcomeRecorder.RecordAsync(
                scheduledJob.JobId,
                claim.ExecutionId,
                success: false,
                errorMessage: exception.Message,
                cancellationToken: cancellationToken
            );
        }

        return await _outcomeRecorder.RecordAsync(
            scheduledJob.JobId,
            claim.ExecutionId,
            success: true,
            errorMessage: null,
            cancellationToken
        );
    }

    private static bool IsMissingJobException(Exception exception)
    {
        return exception is AgwException agwException && agwException.Code == ErrorCodes.JobNotFound.Code;
    }
}
