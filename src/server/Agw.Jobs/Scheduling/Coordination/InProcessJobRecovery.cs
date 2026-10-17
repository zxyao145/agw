using Agw.Jobs.Scheduling.Attempts;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Jobs.Scheduling.Coordination;

public sealed class InProcessJobRecovery
{
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProjectExecutionLock _projectExecutionLock;
    private readonly TimeProvider _timeProvider;
    private readonly JobSchedulerWakeSignal _schedulerWakeSignal;
    private readonly ILogger<InProcessJobRecovery> _logger;

    public InProcessJobRecovery(
        IServiceScopeFactory scopeFactory,
        IProjectExecutionLock projectExecutionLock,
        TimeProvider timeProvider,
        JobSchedulerWakeSignal schedulerWakeSignal,
        ILogger<InProcessJobRecovery> logger
    )
    {
        _scopeFactory = scopeFactory;
        _projectExecutionLock = projectExecutionLock;
        _timeProvider = timeProvider;
        _schedulerWakeSignal = schedulerWakeSignal;
        _logger = logger;
    }

    public async Task RunPeriodicAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(RecoveryInterval, _timeProvider, stoppingToken);
            if (!await JobRecoverySupport.TryRecoverAsync(RecoverAsync, _logger, "in-process", stoppingToken))
            {
                break;
            }
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var runningJobs = await jobStore.ListRunningAsync(cancellationToken);
        await Task.WhenAll(
            runningJobs
                .GroupBy(job => job.ProjectId)
                .Select(jobs => RecoverProjectAsync(jobs.ToArray(), cancellationToken))
        );
    }

    private async Task RecoverProjectAsync(Job[] snapshots, CancellationToken cancellationToken)
    {
        var projectId = snapshots[0].ProjectId;
        var lease = await _projectExecutionLock.TryAcquireAsync(projectId, cancellationToken);
        if (lease == null)
        {
            return;
        }

        await using var projectLease = new ApplicationLockScope(lease, cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var jobStore = services.GetRequiredService<IJobStore>();
        var recorder = services.GetRequiredService<IJobAttemptOutcomeRecorder>();
        foreach (var snapshot in snapshots)
        {
            var currentJob = await jobStore.GetRunningAsync(snapshot.Id, projectLease.Token);
            if (currentJob is null || currentJob.ActiveTurnId != snapshot.ActiveTurnId)
            {
                continue;
            }

            if (JobRecoverySupport.GetActiveTurnId(currentJob, _logger) is not Guid turnId)
            {
                continue;
            }

            var result = await recorder
                .RecordAsync(
                    snapshot.Id,
                    turnId,
                    success: false,
                    "The in-process Job execution was interrupted before its outcome was recorded.",
                    projectLease.Token
                )
                .ConfigureAwait(false);
            if (result is JobAttemptResult.Reschedule)
            {
                _schedulerWakeSignal.NotifyChanged();
            }
        }
    }
}
