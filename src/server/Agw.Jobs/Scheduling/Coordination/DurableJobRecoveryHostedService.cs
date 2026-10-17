using Agw.Jobs.Execution;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
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
    private readonly IServerInitializationState _initializationState;

    public DurableJobRecoveryHostedService(
        IServiceScopeFactory scopeFactory,
        IServerInitializationState initializationState,
        IProjectExecutionLock projectExecutionLock,
        TimeProvider timeProvider,
        JobSchedulerWakeSignal schedulerWakeSignal,
        ILogger<DurableJobRecoveryHostedService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _initializationState = initializationState;
        _projectExecutionLock = projectExecutionLock;
        _timeProvider = timeProvider;
        _schedulerWakeSignal = schedulerWakeSignal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_initializationState.IsInitialized)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await JobRecoverySupport.TryRecoverAsync(RecoverAsync, _logger, "distributed", stoppingToken))
            {
                break;
            }

            await Task.Delay(RecoveryInterval, _timeProvider, stoppingToken);
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var runningJobs = await jobStore.ListRunningAsync(cancellationToken);
        await Task.WhenAll(runningJobs.Select(job => RecoverJobAsync(job, cancellationToken)));
    }

    private async Task RecoverJobAsync(Job job, CancellationToken cancellationToken)
    {
        var lease = await _projectExecutionLock.AcquireAsync(job.ProjectId, cancellationToken);
        await using var projectLock = new ApplicationLockScope(lease, cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var currentJob = await jobStore.GetRunningAsync(job.Id, projectLock.Token);
        if (currentJob == null || currentJob.ActiveTurnId != job.ActiveTurnId)
        {
            return;
        }

        if (JobRecoverySupport.GetActiveTurnId(currentJob, _logger) is not Guid turnId)
        {
            return;
        }

        await RecoverAttemptAsync(scope.ServiceProvider, currentJob, turnId, projectLock.Token);
    }

    private async Task RecoverAttemptAsync(
        IServiceProvider services,
        Job job,
        Guid turnId,
        CancellationToken cancellationToken
    )
    {
        if (!JobAgentExecutor.TryResolveOwnerUserId(job, out var ownerUserId))
        {
            await RecordOutcomeAsync(
                services,
                job.Id,
                turnId,
                success: false,
                "The Job owner is missing.",
                cancellationToken
            );
            return;
        }

        var executionClient = services.GetRequiredService<IDurableAgentExecutionFacade>();
        AgentExecutionResult outcome;
        try
        {
            outcome = await executionClient.GetOutcomeAsync(turnId, ownerUserId, cancellationToken);
        }
        catch (AgwException exception) when (exception.Code == ErrorCodes.DurableExecutionNotFound.Code)
        {
            await RecordOutcomeAsync(
                services,
                job.Id,
                turnId,
                success: false,
                "The durable Job execution was not registered before the scheduler stopped.",
                cancellationToken
            );
            return;
        }

        await RecordExecutionOutcomeAsync(
            services,
            executionClient,
            job.Id,
            turnId,
            ownerUserId,
            outcome,
            cancellationToken
        );
    }

    private async Task RecordExecutionOutcomeAsync(
        IServiceProvider services,
        IDurableAgentExecutionFacade executionClient,
        Guid jobId,
        Guid turnId,
        string ownerUserId,
        AgentExecutionResult outcome,
        CancellationToken cancellationToken
    )
    {
        if (outcome.State is AgentExecutionState.Queued or AgentExecutionState.Running)
        {
            return;
        }

        if (outcome.State == AgentExecutionState.WaitingForHuman)
        {
            await executionClient.InterruptAsync(
                turnId,
                ownerUserId,
                "Scheduled Jobs do not support human interaction.",
                cancellationToken
            );
            await RecordOutcomeAsync(
                services,
                jobId,
                turnId,
                success: false,
                "Scheduled Jobs do not support human interaction.",
                cancellationToken
            );
            return;
        }

        if (outcome.State == AgentExecutionState.Completed)
        {
            await RecordOutcomeAsync(services, jobId, turnId, success: true, errorMessage: null, cancellationToken);
            return;
        }

        await RecordOutcomeAsync(
            services,
            jobId,
            turnId,
            success: false,
            outcome.ErrorMessage ?? "The distributed Job execution did not complete.",
            cancellationToken
        );
    }

    private async Task RecordOutcomeAsync(
        IServiceProvider services,
        Guid jobId,
        Guid turnId,
        bool success,
        string? errorMessage,
        CancellationToken cancellationToken
    )
    {
        var recorder = services.GetRequiredService<IJobAttemptOutcomeRecorder>();
        var result = await recorder
            .RecordAsync(jobId, turnId, success, errorMessage, cancellationToken)
            .ConfigureAwait(false);
        if (result is JobAttemptResult.Reschedule)
        {
            _schedulerWakeSignal.NotifyChanged();
        }
    }
}
