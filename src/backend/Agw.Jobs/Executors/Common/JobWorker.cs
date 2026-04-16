using Agw.Jobs.Application.Services;
using Agw.Jobs.Dtos;
using Agw.Jobs.External;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Jobs.Executors.Common;

public sealed class JobWorker(
    IServiceScopeFactory scopeFactory,
    IProjectExecutionLock projectExecutionLock,
    IOptions<JobWorkerOptions> options,
    ILogger<JobWorker> logger) : IJobWorker
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IProjectExecutionLock _projectExecutionLock = projectExecutionLock;
    private readonly JobWorkerOptions _options = options.Value;
    private readonly ILogger<JobWorker> _logger = logger;

    public async Task<JobWorkerExecutionResult> ExecuteAsync(InMemoryJob job, CancellationToken cancellationToken)
    {
        await using var projectLock = await _projectExecutionLock.AcquireAsync(job.ProjectId, cancellationToken);

        var start = DateTimeOffset.UtcNow;
        using var scope = _scopeFactory.CreateScope();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var timeCalculator = scope.ServiceProvider.GetRequiredService<IJobTimeCalculator>();
        var agentExecutor = scope.ServiceProvider.GetRequiredService<IAgentExecutor>();

        Guid taskId = Guid.Empty;
        try
        {
            var markedRunning = await jobStore.MarkRunningAsync(job.JobId, cancellationToken);
            if (!markedRunning)
            {
                _logger.LogInformation(
                    "Job {JobId} is no longer enabled/pending. Dropping stale in-memory entry.",
                    job.JobId);
                return JobWorkerExecutionResult.Remove(job.JobId);
            }

            var persistedJob = InMemoryJobMapper.ToJob(job);
            taskId = await agentExecutor.ExecuteAsync(persistedJob, cancellationToken);

            var nextRunTime = timeCalculator.GetNextRunTime(persistedJob, DateTimeOffset.UtcNow);
            await jobStore.MarkSucceededAsync(job.JobId, nextRunTime, cancellationToken);
            await jobStore.AddExecutionLogAsync(
                job.JobId,
                taskId,
                start,
                DateTimeOffset.UtcNow,
                success: true,
                attempt: job.RetryCount + 1,
                errorMessage: null,
                cancellationToken);

            return nextRunTime.HasValue
                ? JobWorkerExecutionResult.Schedule(job.JobId, nextRunTime.Value, retryCount: 0)
                : JobWorkerExecutionResult.Remove(job.JobId);
        }
        catch (Exception ex)
        {
            if (IsMissingJobException(ex))
            {
                _logger.LogWarning("Job {JobId} no longer exists. Dropping stale in-memory entry.", job.JobId);
                return JobWorkerExecutionResult.Remove(job.JobId);
            }

            _logger.LogError(ex, "Job {JobId} execution failed.", job.JobId);
            var retryCount = job.RetryCount + 1;

            if (retryCount <= job.MaxRetryCount)
            {
                var nextRunTime = DateTimeOffset.UtcNow.Add(_options.RetryDelay);
                try
                {
                    await jobStore.MarkRetryAsync(job.JobId, nextRunTime, retryCount, ex.Message, cancellationToken);
                    await jobStore.AddExecutionLogAsync(
                        job.JobId,
                        taskId,
                        start,
                        DateTimeOffset.UtcNow,
                        success: false,
                        attempt: retryCount,
                        errorMessage: ex.Message,
                        cancellationToken);
                }
                catch (Exception bookkeepingEx) when (IsMissingJobException(bookkeepingEx))
                {
                    _logger.LogWarning("Job {JobId} disappeared during retry bookkeeping. Dropping stale in-memory entry.", job.JobId);
                    return JobWorkerExecutionResult.Remove(job.JobId);
                }

                return JobWorkerExecutionResult.Schedule(job.JobId, nextRunTime, retryCount);
            }

            try
            {
                await jobStore.MarkFailedAsync(job.JobId, retryCount, ex.Message, cancellationToken);
                await jobStore.AddExecutionLogAsync(
                    job.JobId,
                    taskId,
                    start,
                    DateTimeOffset.UtcNow,
                    success: false,
                    attempt: retryCount,
                    errorMessage: ex.Message,
                    cancellationToken);
            }
            catch (Exception bookkeepingEx) when (IsMissingJobException(bookkeepingEx))
            {
                _logger.LogWarning("Job {JobId} disappeared during failure bookkeeping. Dropping stale in-memory entry.", job.JobId);
            }

            return JobWorkerExecutionResult.Remove(job.JobId);
        }
    }

    private static bool IsMissingJobException(Exception exception)
    {
        return exception is AgwException agwException
            && agwException.Code == ErrorCodes.JobNotFound.Code;
    }
}
