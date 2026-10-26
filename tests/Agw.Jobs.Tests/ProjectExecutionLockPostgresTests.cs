using System.Data.Common;
using System.Security.Claims;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Jobs;
using Agw.Infrastructure.Projects;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Projects.Application;
using Agw.Projects.Application.Facades;
using Agw.Projects.Domain.Services;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Testing;
using Medallion.Threading.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Agw.Jobs.Tests;

[Trait("Category", "Postgres")]
public sealed class ProjectExecutionLockPostgresTests
{
    [Fact]
    public async Task AcquireAsync_PostgresLeaseLoss_CancelsHeldOperation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adminConnectionString = PostgresJobTestDatabase.RequireAdminConnectionString();

        using var owner = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
        );
        await using var database = await PostgresJobTestDatabase.CreateAsync(adminConnectionString, cancellationToken);
        var projectId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Name = "interrupted-postgres-job",
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            NextRunTime = TimeProvider.System.GetUtcNow(),
            Status = JobStatus.Running,
            ActiveTurnId = turnId,
            ActiveAttemptStartedAt = TimeProvider.System.GetUtcNow(),
            CreateBy = "owner",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };
        await using (var seed = database.CreateContext())
        {
            seed.Jobs.Add(job);
            await seed.SaveChangesAsync(cancellationToken);
        }

        var applicationName = $"agw-jobs-lock-{Guid.CreateVersion7():N}";
        var lockConnectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = applicationName,
            Pooling = false,
        };
        var provider = new PostgresDistributedSynchronizationProvider(
            lockConnectionString.ConnectionString,
            options => options.KeepaliveCadence(TimeSpan.FromSeconds(1))
        );
        var projectLock = new DistributedProjectExecutionLock(provider);
        var lease = await projectLock.AcquireAsync(projectId, cancellationToken);
        var handleLostToken = lease.HandleLostToken;
        Assert.True(handleLostToken.CanBeCanceled);
        Assert.Null(await projectLock.TryAcquireAsync(projectId, cancellationToken));

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            handleLostToken
        );
        var heldOperation = Task.Delay(Timeout.InfiniteTimeSpan, operationCancellation.Token);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            var backendPid = 0;
            for (var attempt = 0; attempt < 100 && backendPid == 0; attempt++)
            {
                await using var find = new NpgsqlCommand(
                    "SELECT pid FROM pg_stat_activity WHERE application_name = @application_name AND pid <> pg_backend_pid()",
                    connection
                );
                find.Parameters.AddWithValue("application_name", applicationName);
                var result = await find.ExecuteScalarAsync(cancellationToken);
                if (result is int pid)
                {
                    backendPid = pid;
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            Assert.True(backendPid > 0);
            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection);
            terminate.Parameters.AddWithValue("pid", backendPid);
            Assert.True((bool)(await terminate.ExecuteScalarAsync(cancellationToken))!);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            heldOperation.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
        );
        Assert.True(handleLostToken.IsCancellationRequested);
        await lease.DisposeAsync();

        var timeProvider = new TestTimeProvider(TimeProvider.System.GetUtcNow().AddMinutes(1));
        await using var outcomeContext = database.CreateContext();
        var userInfo = new UserInfoService();
        var projectResolver = new ProjectResolver(outcomeContext, userInfo);
        var taskExecution = new TaskExecutionAppService(
            outcomeContext,
            projectResolver,
            new ConversationHistoryDomainService(),
            timeProvider,
            userInfo,
            new ConversationCreatorStore(outcomeContext)
        );
        var taskResolver = new TaskAppService(outcomeContext, projectResolver, taskExecution, userInfo);
        var recorder = new JobAttemptOutcomeRecorder(
            outcomeContext,
            new JobOutcomeTransaction(outcomeContext, InMemoryApplicationLock.Shared),
            new ProjectTaskFacade(taskExecution, outcomeContext, taskResolver, userInfo),
            timeProvider
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recorder.RecordAsync(job.Id, turnId, success: false, "stale outcome", operationCancellation.Token)
        );

        var reacquired = await projectLock.TryAcquireAsync(projectId, cancellationToken);
        Assert.NotNull(reacquired);
        await using var reacquiredLease = reacquired;
        outcomeContext.ChangeTracker.Clear();
        var persistedJob = await outcomeContext.Jobs.SingleAsync(item => item.Id == job.Id, cancellationToken);
        var logs = await outcomeContext.JobLogs.Where(item => item.JobId == job.Id).ToListAsync(cancellationToken);
        Assert.Equal(JobStatus.Running, persistedJob.Status);
        Assert.Equal(turnId, persistedJob.ActiveTurnId);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task RecoverAsync_ActiveTurnChangesAfterSnapshot_DoesNotRecordStaleOutcome()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adminConnectionString = PostgresJobTestDatabase.RequireAdminConnectionString();

        using var owner = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
        );
        var snapshotInterceptor = new RunningJobSnapshotInterceptor();
        await using var database = await PostgresJobTestDatabase.CreateAsync(
            adminConnectionString,
            cancellationToken,
            snapshotInterceptor
        );
        var clock = new TestTimeProvider(TimeProvider.System.GetUtcNow().AddMinutes(1));
        var oldTurnId = Guid.CreateVersion7();
        var newTurnId = Guid.CreateVersion7();
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            Name = "snapshot-race-job",
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            NextRunTime = clock.GetUtcNow(),
            Status = JobStatus.Running,
            ActiveTurnId = oldTurnId,
            ActiveAttemptStartedAt = clock.GetUtcNow().AddMinutes(-1),
            CreateBy = "owner",
            CreateTime = clock.GetUtcNow().AddMinutes(-1),
        };
        await using (var seed = database.CreateContext())
        {
            seed.Jobs.Add(job);
            await seed.SaveChangesAsync(cancellationToken);
        }

        var executionLock = new InMemoryProjectExecutionLock();
        var services = new ServiceCollection();
        JobTestServices.AddRecoveryServices(services, database.Options, clock, executionLock);
        await using var provider = services.BuildServiceProvider();
        await provider
            .GetRequiredService<Agw.Setup.Services.DatabaseInitializationStateStore>()
            .PersistAsync("test-password-hash", cancellationToken);
        var recovery = provider.GetRequiredService<InProcessJobRecovery>();
        var heldLease = await executionLock.AcquireAsync(job.ProjectId, cancellationToken);
        var leaseReleased = false;
        var recoveryTask = recovery.RecoverAsync(cancellationToken);
        try
        {
            await snapshotInterceptor.WaitForSnapshotAsync(cancellationToken);
            await using (var update = database.CreateContext())
            {
                var current = await update.Jobs.SingleAsync(item => item.Id == job.Id, cancellationToken);
                current.ActiveTurnId = newTurnId;
                current.ActiveAttemptStartedAt = clock.GetUtcNow();
                await update.SaveChangesAsync(cancellationToken);
            }

            await heldLease.DisposeAsync();
            leaseReleased = true;
            snapshotInterceptor.ReleaseSnapshot();
            await recoveryTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        finally
        {
            snapshotInterceptor.ReleaseSnapshot();
            if (!leaseReleased)
            {
                await heldLease.DisposeAsync();
            }
        }

        await using var verification = database.CreateContext();
        var persisted = await verification
            .Jobs.AsNoTracking()
            .SingleAsync(item => item.Id == job.Id, cancellationToken);
        var logs = await verification
            .JobLogs.AsNoTracking()
            .Where(item => item.JobId == job.Id)
            .ToListAsync(cancellationToken);
        Assert.Equal(JobStatus.Running, persisted.Status);
        Assert.Equal(newTurnId, persisted.ActiveTurnId);
        Assert.Empty(logs);
    }

    private sealed class RunningJobSnapshotInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _snapshotRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continueSnapshot = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _paused;
        private string? _lastReaderCommandText;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            _lastReaderCommandText = command.CommandText;
            if (
                command.CommandText.Contains("FROM job", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains(".status", StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _paused, 1, 0) == 0
            )
            {
                _snapshotRead.TrySetResult();
                await _continueSnapshot.Task.WaitAsync(cancellationToken);
            }

            return result;
        }

        public async Task WaitForSnapshotAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _snapshotRead.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"No Running Job snapshot query was intercepted. Last reader command: {_lastReaderCommandText}",
                    exception
                );
            }
        }

        public void ReleaseSnapshot() => _continueSnapshot.TrySetResult();
    }
}
