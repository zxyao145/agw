using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using System.Threading.Channels;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Jobs;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Setup.Services;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Testing;
using Medallion.Threading.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Jobs.Tests;

public sealed class JobHostedServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatch_InfrastructureFailure_NextJobInProjectCanRun(bool lockFailure)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        var job = await SeedAsync(database, TimeProvider.System.GetUtcNow(), token);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Provider"] = lockFailure ? "postgres" : "inmemory",
                    ["ConnectionString"] = "Host=127.0.0.1;Port=1;Database=postgres;Username=unused;Timeout=1",
                }
            )
            .Build();
        using var lockServices = new ServiceCollection()
            .AddOptions()
            .Configure<DatabaseSettings>(_ => { })
            .Configure<DistributedLockSettings>(configuration)
            .BuildServiceProvider();
        var projectLock = new ProjectExecutionLockRouter(
            lockServices.GetRequiredService<IOptionsMonitor<DatabaseSettings>>(),
            lockServices.GetRequiredService<IOptionsMonitor<DistributedLockSettings>>(),
            new InMemoryProjectExecutionLock(),
            (_, connection) => new PostgresDistributedSynchronizationProvider(connection)
        );
        await using var provider = JobTestServices.Create(database.Options, TimeProvider.System, projectLock);
        using var scheduler = ActivatorUtilities.CreateInstance<JobHostedService>(provider);
        await using var setup = database.CreateContext();
        if (lockFailure is false)
            await setup.Database.ExecuteSqlRawAsync("ALTER TABLE job RENAME TO unavailable_job", token);

        Invoke(scheduler, "DispatchOrQueueByProject", ScheduledJob.FromJob(job), token);
        await WaitForExecutionsAsync(scheduler);
        var queued = (ConcurrentDictionary<Guid, ScheduledJob>)Field(scheduler, "_jobMap");
        Assert.Contains(job.Id, queued.Keys);
        configuration["Provider"] = "inmemory";
        configuration.Reload();
        if (lockFailure is false)
            await setup.Database.ExecuteSqlRawAsync("ALTER TABLE unavailable_job RENAME TO job", token);

        Invoke(scheduler, "DispatchOrQueueByProject", ScheduledJob.FromJob(job), token);
        await WaitForExecutionsAsync(scheduler);
        using var verificationScope = UserInfoUtil.PushSystemScope();
        Assert.Equal(JobStatus.Paused, (await setup.Jobs.SingleAsync(token)).Status);
        Assert.DoesNotContain(job.Id, queued.Keys);
    }

    [Fact]
    public async Task Dispatch_ProjectChangedAfterPrefetch_RefreshesSnapshotWithoutStartingAttempt()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        var original = await SeedAsync(database, TimeProvider.System.GetUtcNow(), token);
        var movedProjectId = Guid.CreateVersion7();
        using (UserInfoUtil.PushSystemScope())
        {
            await using var update = database.CreateContext();
            var persisted = await update.Jobs.SingleAsync(job => job.Id == original.Id, token);
            persisted.ProjectId = movedProjectId;
            await update.SaveChangesAsync(token);
        }

        await using var provider = JobTestServices.Create(
            database.Options,
            TimeProvider.System,
            new InMemoryProjectExecutionLock()
        );
        using var scheduler = ActivatorUtilities.CreateInstance<JobHostedService>(provider);
        var scheduledSnapshot = ScheduledJob.FromJob(original);
        Invoke(scheduler, "UpsertScheduledJob", scheduledSnapshot);
        Invoke(scheduler, "DispatchOrQueueByProject", scheduledSnapshot, token);
        await WaitForExecutionsAsync(scheduler);

        var queued = (ConcurrentDictionary<Guid, ScheduledJob>)Field(scheduler, "_jobMap");
        Assert.True(queued.TryGetValue(original.Id, out var refreshed));
        Assert.Equal(movedProjectId, refreshed!.ProjectId);
        Assert.True(refreshed.Version > scheduledSnapshot.Version);
        var runningProjects = (ConcurrentDictionary<Guid, byte>)Field(scheduler, "_runningProjects");
        Assert.DoesNotContain(original.ProjectId, runningProjects.Keys);

        using (UserInfoUtil.PushSystemScope())
        await using (var verification = database.CreateContext())
        {
            var persisted = await verification.Jobs.SingleAsync(job => job.Id == original.Id, token);
            Assert.Equal(JobStatus.Pending, persisted.Status);
            Assert.Null(persisted.ActiveTurnId);
            Assert.Null(persisted.ActiveAttemptStartedAt);
            Assert.Empty(await verification.JobLogs.Where(log => log.JobId == original.Id).ToListAsync(token));
        }
    }

    [Fact]
    public async Task RunExecuteLoop_TimerWins_NextDueJobNeedsOnlyOneWake()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        var first = await SeedAsync(database, clock.GetUtcNow().AddSeconds(10), token);
        var second = await SeedAsync(database, clock.GetUtcNow().AddSeconds(10), token);
        var observer = new ClaimReadObserver();
        var options = new DbContextOptionsBuilder<AgwDbContext>(database.Options).AddInterceptors(observer).Options;
        await using var provider = JobTestServices.Create(options, clock, new InMemoryProjectExecutionLock());
        using var scheduler = ActivatorUtilities.CreateInstance<JobHostedService>(provider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Invoke(scheduler, "UpsertScheduledJob", ScheduledJob.FromJob(first));
        ((SemaphoreSlim)Field(scheduler, "_wakeSignal")).Wait(0, token);
        var loop = (Task)Invoke(scheduler, "RunExecuteLoopAsync", cancellation.Token)!;
        try
        {
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(10), token);
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(
                first.Id,
                await observer.Claims.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token)
            );
            Invoke(scheduler, "UpsertScheduledJob", ScheduledJob.FromJob(second));
            Assert.Equal(
                second.Id,
                await observer.Claims.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token)
            );
        }
        finally
        {
            await cancellation.CancelAsync();
            var failure = await Record.ExceptionAsync(() => loop);
            if (failure != null)
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
            await WaitForExecutionsAsync(scheduler);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunExecuteLoop_FarFutureJob_ImmediateJobStillDispatches(bool useMaxValue)
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        await using (var schema = database.CreateContext())
        {
            await schema.Database.EnsureCreatedAsync(token);
        }
        await using var provider = JobTestServices.Create(database.Options, clock, new InMemoryProjectExecutionLock());
        using var scheduler = ActivatorUtilities.CreateInstance<JobHostedService>(provider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var farFuture = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            Name = "far-future-job",
            IsEnabled = true,
            Status = JobStatus.Pending,
            NextRunTime = useMaxValue ? DateTimeOffset.MaxValue : clock.GetUtcNow().AddDays(60),
            TriggerType = TriggerType.Once,
            TriggerValue = string.Empty,
        };
        var immediate = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            Name = "missing-immediate-job",
            IsEnabled = true,
            Status = JobStatus.Pending,
            NextRunTime = clock.GetUtcNow(),
            TriggerType = TriggerType.Once,
            TriggerValue = string.Empty,
        };
        Invoke(scheduler, "UpsertScheduledJob", ScheduledJob.FromJob(farFuture));
        var loop = (Task)Invoke(scheduler, "RunExecuteLoopAsync", cancellation.Token)!;
        try
        {
            var timerReady = clock.WaitForTimerAsync(JobSchedulingDefaults.PrefetchInterval, token);
            Assert.Same(timerReady, await Task.WhenAny(timerReady, loop).WaitAsync(TimeSpan.FromSeconds(10), token));
            await timerReady;

            Invoke(scheduler, "UpsertScheduledJob", ScheduledJob.FromJob(immediate));
            var queued = (ConcurrentDictionary<Guid, ScheduledJob>)Field(scheduler, "_jobMap");
            for (var attempt = 0; attempt < 100 && queued.ContainsKey(immediate.Id); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), token);
            }

            Assert.DoesNotContain(immediate.Id, queued.Keys);
            Assert.Contains(farFuture.Id, queued.Keys);
        }
        finally
        {
            await cancellation.CancelAsync();
            var failure = await Record.ExceptionAsync(() => loop);
            if (failure != null)
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
            await WaitForExecutionsAsync(scheduler);
        }
    }

    [Fact]
    public async Task StopAsync_RunningQueue_WaitsForExecutionCleanup()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        await SeedAsync(database, TimeProvider.System.GetUtcNow(), token);
        var gate = new DatabaseCommandGate(IsClaimQuery, holdCleanup: true);
        var options = new DbContextOptionsBuilder<AgwDbContext>(database.Options).AddInterceptors(gate).Options;
        await using var provider = JobTestServices.Create(
            options,
            TimeProvider.System,
            new InMemoryProjectExecutionLock()
        );
        await provider.GetRequiredService<DatabaseInitializationStateStore>().PersistAsync("test-password-hash", token);
        using var scheduler = ActivatorUtilities.CreateInstance<JobHostedService>(provider);
        await scheduler.StartAsync(token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var stopping = scheduler.StopAsync(token);
        try
        {
            await gate.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            gate.FinishCleanup();
            gate.Release();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
    }

    internal static Task ExecuteOneAsync(JobHostedService scheduler, ScheduledJob job, CancellationToken token) =>
        (Task)Invoke(scheduler, "ExecuteOneAsync", job, token)!;

    private static async Task<Job> SeedAsync(
        SqliteJobTestDatabase database,
        DateTimeOffset dueAt,
        CancellationToken token
    )
    {
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            Name = "unowned-job",
            IsEnabled = true,
            Status = JobStatus.Pending,
            NextRunTime = dueAt,
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            CreateBy = string.Empty,
        };
        using var seedScope = UserInfoUtil.PushSystemScope();
        await using var seed = database.CreateContext();
        await seed.Database.EnsureCreatedAsync(token);
        seed.Jobs.Add(job);
        await seed.SaveChangesAsync(token);
        return job;
    }

    private static Task WaitForExecutionsAsync(JobHostedService scheduler) =>
        Task.WhenAll(((ConcurrentDictionary<Guid, Task>)Field(scheduler, "_runningExecutions")).Values);

    private static bool IsClaimQuery(DbCommand command) =>
        command.CommandText.Contains("is_enabled", StringComparison.Ordinal)
        && command
            .Parameters.Cast<DbParameter>()
            .Any(parameter => parameter.ParameterName.Contains("JobId", StringComparison.OrdinalIgnoreCase));

    private static object Field(object instance, string name) =>
        typeof(JobHostedService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static object? Invoke(object instance, string name, params object[] args) =>
        typeof(JobHostedService)
            .GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                args.Select(arg => arg.GetType()).ToArray(),
                null
            )!
            .Invoke(instance, args);

    private sealed class ClaimReadObserver : DbCommandInterceptor
    {
        public Channel<Guid> Claims { get; } = Channel.CreateUnbounded<Guid>();

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            if (IsClaimQuery(command))
            {
                var parameter = command
                    .Parameters.Cast<DbParameter>()
                    .Single(item => item.ParameterName.Contains("JobId", StringComparison.OrdinalIgnoreCase));
                Claims.Writer.TryWrite(Guid.Parse(parameter.Value!.ToString()!));
            }
            return ValueTask.FromResult(result);
        }
    }
}
