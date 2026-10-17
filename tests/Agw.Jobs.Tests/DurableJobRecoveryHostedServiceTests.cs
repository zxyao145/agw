using System.Reflection;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Jobs;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Setup.Services;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs.Tests;

public sealed class DurableJobRecoveryHostedServiceTests
{
    [Theory]
    [InlineData("owner", "was not registered", JobStatus.Pending)]
    [InlineData("", "owner is missing", JobStatus.Paused)]
    public async Task RecoverJobAsync_InterruptedAttempt_RecordsFailureAndClearsAttempt(
        string owner,
        string expectedError,
        JobStatus expectedStatus
    )
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        var job = await SeedAsync(database, owner, token);
        await using var provider = JobTestServices.Create(
            database.Options,
            TimeProvider.System,
            new InMemoryProjectExecutionLock()
        );
        using var service = ActivatorUtilities.CreateInstance<DurableJobRecoveryHostedService>(provider);

        Assert.False(UserInfoUtil.IsContextActive);
        await RecoverJobAsync(service, job, token);

        using var verificationScope = UserInfoUtil.PushSystemScope();
        await using var verification = database.CreateContext();
        var persisted = await verification.Jobs.SingleAsync(token);
        var log = await verification.JobLogs.SingleAsync(token);
        Assert.Equal(job.ActiveTurnId, log.TaskId);
        Assert.False(log.Success);
        Assert.Contains(expectedError, log.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(expectedStatus, persisted.Status);
        Assert.Null(persisted.ActiveTurnId);
    }

    [Fact]
    public async Task RecoverJobAsync_ActiveTurnChangesWhileWaiting_PreservesNewAttempt()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        var job = await SeedAsync(database, "owner", token);
        var projectLock = new InMemoryProjectExecutionLock();
        await using var provider = JobTestServices.Create(database.Options, TimeProvider.System, projectLock);
        using var service = ActivatorUtilities.CreateInstance<DurableJobRecoveryHostedService>(provider);
        var newTurnId = Guid.CreateVersion7();
        Task recovery;
        await using (var held = await projectLock.AcquireAsync(job.ProjectId, token))
        {
            recovery = RecoverJobAsync(service, job, token);
            Assert.False(recovery.IsCompleted);
            using var updateScope = UserInfoUtil.PushSystemScope();
            await using var update = database.CreateContext();
            var current = await update.Jobs.SingleAsync(token);
            current.ActiveTurnId = newTurnId;
            await update.SaveChangesAsync(token);
        }

        await recovery.WaitAsync(TimeSpan.FromSeconds(10), token);

        using var verificationScope = UserInfoUtil.PushSystemScope();
        await using var verification = database.CreateContext();
        var persisted = await verification.Jobs.SingleAsync(token);
        Assert.Equal(JobStatus.Running, persisted.Status);
        Assert.Equal(newTurnId, persisted.ActiveTurnId);
        Assert.Empty(await verification.JobLogs.ToListAsync(token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_BeforeInitialization_WaitsWithoutRecoveringJobs(bool finishSetup)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        var job = await SeedAsync(database, "owner", token);
        var clock = new ManualTimeProvider();
        await using var provider = JobTestServices.Create(database.Options, clock, new InMemoryProjectExecutionLock());
        using var service = ActivatorUtilities.CreateInstance<DurableJobRecoveryHostedService>(provider);
        await service.StartAsync(token);
        try
        {
            await clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(250), token);
            using (UserInfoUtil.PushSystemScope())
            await using (var verification = database.CreateContext())
            {
                Assert.Equal(JobStatus.Running, (await verification.Jobs.SingleAsync(token)).Status);
                Assert.Empty(await verification.JobLogs.ToListAsync(token));
            }
            if (finishSetup)
            {
                await provider
                    .GetRequiredService<DatabaseInitializationStateStore>()
                    .PersistAsync("test-password-hash", token);
                clock.Advance(TimeSpan.FromMilliseconds(250));
                await clock.WaitForTimerAsync(TimeSpan.FromSeconds(30), token);
                using var verificationScope = UserInfoUtil.PushSystemScope();
                await using var verification = database.CreateContext();
                Assert.Equal(job.ActiveTurnId, (await verification.JobLogs.SingleAsync(token)).TaskId);
            }
        }
        finally
        {
            await service.StopAsync(token);
        }
    }

    internal static Task RecoverJobAsync(DurableJobRecoveryHostedService service, Job job, CancellationToken token) =>
        (Task)
            typeof(DurableJobRecoveryHostedService)
                .GetMethod("RecoverJobAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [job, token])!;

    private static async Task<Job> SeedAsync(SqliteJobTestDatabase database, string owner, CancellationToken token)
    {
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            Name = "interrupted",
            Status = JobStatus.Running,
            IsEnabled = true,
            MaxRetryCount = 3,
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            ActiveTurnId = Guid.CreateVersion7(),
            ActiveAttemptStartedAt = TimeProvider.System.GetUtcNow(),
            CreateBy = owner,
        };
        using var seedScope = UserInfoUtil.PushSystemScope();
        await using var seed = database.CreateContext();
        await seed.Database.EnsureCreatedAsync(token);
        seed.Jobs.Add(job);
        await seed.SaveChangesAsync(token);
        return job;
    }
}
