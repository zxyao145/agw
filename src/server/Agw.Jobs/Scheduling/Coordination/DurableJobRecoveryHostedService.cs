using Agw.Auth.Contracts;
using Agw.Jobs.Execution;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Jobs.Scheduling.Coordination;

public sealed class DurableJobRecoveryHostedService : BackgroundService
{
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProjectExecutionLock _projectExecutionLock;
    private readonly TimeProvider _timeProvider;
    private readonly JobSchedulerWakeSignal _schedulerWakeSignal;
    private readonly ILogger<DurableJobRecoveryHostedService> _logger;

    public DurableJobRecoveryHostedService(
        IServiceScopeFactory scopeFactory,
        IProjectExecutionLock projectExecutionLock,
        TimeProvider timeProvider,
        JobSchedulerWakeSignal schedulerWakeSignal,
        ILogger<DurableJobRecoveryHostedService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _projectExecutionLock = projectExecutionLock;
        _timeProvider = timeProvider;
        _schedulerWakeSignal = schedulerWakeSignal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to recover distributed Job executions.");
            }

            await Task.Delay(RecoveryInterval, _timeProvider, stoppingToken);
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IRepository<Job>>();
        IReadOnlyList<Job> runningJobs;
        using (UserInfoUtil.PushSystemScope())
        {
            runningJobs = await jobRepository.ListAsync(job => job.Status == JobStatus.Running);
        }
        await Task.WhenAll(runningJobs.Select(job => RecoverJobAsync(job, cancellationToken)));
    }

    private async Task RecoverJobAsync(Job job, CancellationToken cancellationToken)
    {
        await using var projectLock = await _projectExecutionLock.AcquireAsync(job.ProjectId, cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IRepository<Job>>();
        Job? currentJob;
        using (UserInfoUtil.PushSystemScope())
        {
            currentJob = await jobRepository.GetByIdAsync(job.Id);
        }
        if (currentJob?.Status != JobStatus.Running)
        {
            return;
        }

        if (!currentJob.ActiveExecutionId.HasValue || !currentJob.ActiveAttemptStartedAt.HasValue)
        {
            _logger.LogError("Running Job {JobId} does not have a persisted active attempt.", currentJob.Id);
            return;
        }

        var executionId = currentJob.ActiveExecutionId.Value;
        if (!JobAgentExecutor.TryResolveOwnerUserId(currentJob, out var ownerUserId))
        {
            await RecordOutcomeAsync(
                scope.ServiceProvider,
                currentJob.Id,
                executionId,
                success: false,
                "The Job owner is missing.",
                cancellationToken
            );
            return;
        }

        var executionClient = scope.ServiceProvider.GetRequiredService<IDurableAgentExecutionFacade>();
        AgentExecutionResult outcome;
        try
        {
            outcome = await executionClient.GetOutcomeAsync(executionId, ownerUserId, cancellationToken);
        }
        catch (AgwException exception) when (exception.Code == ErrorCodes.DurableExecutionNotFound.Code)
        {
            await RecordOutcomeAsync(
                scope.ServiceProvider,
                currentJob.Id,
                executionId,
                success: false,
                "The durable Job execution was not registered before the scheduler stopped.",
                cancellationToken
            );
            return;
        }

        if (outcome.State is AgentExecutionState.Queued or AgentExecutionState.Running)
        {
            return;
        }

        if (outcome.State == AgentExecutionState.WaitingForHuman)
        {
            await executionClient.InterruptAsync(
                executionId,
                ownerUserId,
                "Scheduled Jobs do not support human interaction.",
                cancellationToken
            );
            await RecordOutcomeAsync(
                scope.ServiceProvider,
                currentJob.Id,
                executionId,
                success: false,
                "Scheduled Jobs do not support human interaction.",
                cancellationToken
            );
            return;
        }

        if (outcome.State == AgentExecutionState.Completed)
        {
            await RecordOutcomeAsync(
                scope.ServiceProvider,
                currentJob.Id,
                executionId,
                success: true,
                errorMessage: null,
                cancellationToken
            );
            return;
        }

        await RecordOutcomeAsync(
            scope.ServiceProvider,
            currentJob.Id,
            executionId,
            success: false,
            outcome.ErrorMessage ?? "The distributed Job execution did not complete.",
            cancellationToken
        );
    }

    private async Task RecordOutcomeAsync(
        IServiceProvider services,
        Guid jobId,
        Guid executionId,
        bool success,
        string? errorMessage,
        CancellationToken cancellationToken
    )
    {
        var recorder = services.GetRequiredService<IJobAttemptOutcomeRecorder>();
        var result = await recorder
            .RecordAsync(jobId, executionId, success, errorMessage, cancellationToken)
            .ConfigureAwait(false);
        if (result is JobAttemptResult.Reschedule)
        {
            _schedulerWakeSignal.NotifyChanged();
        }
    }
}
