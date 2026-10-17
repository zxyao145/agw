using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Jobs;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Setup.Services;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agw.Jobs.Tests;

public sealed class InProcessJobRecoveryTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Host_StartAsync_BeforeInitialization_ReturnsWithoutRecoveringJobs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        using (PushOwner())
        await using (var seed = database.CreateContext())
        {
            await seed.Database.EnsureCreatedAsync(cancellationToken);
            seed.Jobs.Add(
                new Job
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = Guid.CreateVersion7(),
                    Name = "waiting-for-initialization",
                    TriggerType = TriggerType.Interval,
                    TriggerValue = "00:15:00",
                    NextRunTime = StartedAt,
                    Status = JobStatus.Running,
                    ActiveTurnId = Guid.CreateVersion7(),
                    ActiveAttemptStartedAt = StartedAt,
                    CreateBy = "owner",
                    CreateTime = StartedAt,
                }
            );
            await seed.SaveChangesAsync(cancellationToken);
        }

        var clock = new ManualTimeProvider();
        var executionLock = new InMemoryProjectExecutionLock();
        using var host = new HostBuilder()
            .ConfigureServices(
                (_, services) =>
                {
                    JobTestServices.AddRecoveryServices(services, database.Options, clock, executionLock);
                }
            )
            .Build();

        Assert.False(UserInfoUtil.IsContextActive);
        await host.StartAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        using (PushOwner())
        await using (var verification = database.CreateContext())
        {
            Assert.Equal(
                JobStatus.Running,
                await verification
                    .Jobs.AsNoTracking()
                    .Where(item => item.Name == "waiting-for-initialization")
                    .Select(item => item.Status)
                    .SingleAsync(cancellationToken)
            );
            Assert.Empty(await verification.JobLogs.AsNoTracking().ToListAsync(cancellationToken));
        }
        await host.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task StartAsync_InterruptedAttempt_IsRecordedBeforeScheduling()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        var turnId = Guid.CreateVersion7();
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            Name = "interrupted-in-process-job",
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            NextRunTime = StartedAt,
            Status = JobStatus.Running,
            IsEnabled = true,
            MaxRetryCount = 3,
            ActiveTurnId = turnId,
            ActiveAttemptStartedAt = StartedAt,
            CreateBy = "owner",
            CreateTime = StartedAt,
        };
        using (PushOwner())
        await using (var seed = database.CreateContext())
        {
            await seed.Database.EnsureCreatedAsync(cancellationToken);
            seed.Jobs.Add(job);
            await seed.SaveChangesAsync(cancellationToken);
        }

        var clock = new TestTimeProvider(StartedAt.AddMinutes(1));
        var executionLock = new InMemoryProjectExecutionLock();
        var services = new ServiceCollection();
        JobTestServices.AddRecoveryServices(services, database.Options, clock, executionLock);
        await using var provider = services.BuildServiceProvider();
        await provider
            .GetRequiredService<DatabaseInitializationStateStore>()
            .PersistAsync("test-password-hash", cancellationToken);
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        var scheduler = hostedServices.OfType<JobHostedService>().Single();

        Assert.False(UserInfoUtil.IsContextActive);
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(cancellationToken);
        }
        try
        {
            Job? recovered = null;
            IReadOnlyList<JobLog> logs = [];
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (scheduler.ExecuteTask is { IsFaulted: true } executionTask)
                {
                    await executionTask;
                }

                using var verificationUser = PushOwner();
                await using var verification = database.CreateContext();
                recovered = await verification
                    .Jobs.AsNoTracking()
                    .SingleAsync(item => item.Id == job.Id, cancellationToken);
                logs = await verification
                    .JobLogs.AsNoTracking()
                    .Where(item => item.JobId == job.Id)
                    .ToListAsync(cancellationToken);
                if (recovered.Status != JobStatus.Running && logs.Count > 0)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }

            Assert.Equal(JobStatus.Pending, recovered!.Status);
            Assert.Equal(1, recovered.RetryCount);
            Assert.Null(recovered.ActiveTurnId);
            Assert.Null(recovered.ActiveAttemptStartedAt);
            var log = Assert.Single(logs);
            Assert.Equal(turnId, log.TaskId);
            Assert.False(log.Success);
            Assert.Equal(1, log.Attempt);
            Assert.Contains("interrupted", log.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var hostedService in hostedServices.Reverse())
            {
                await hostedService.StopAsync(cancellationToken);
            }
        }
    }

    [Fact]
    public async Task RunPeriodicAsync_ProjectLocked_RecoversOtherProjects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider();
        var projectA = Guid.CreateVersion7();
        var projectB = Guid.CreateVersion7();
        var jobs = new[]
        {
            CreateRunningJob(projectA, "live-project-a", clock.GetUtcNow().AddMinutes(-1)),
            CreateRunningJob(projectB, "orphan-project-b-one", clock.GetUtcNow().AddMinutes(-1)),
            CreateRunningJob(projectB, "orphan-project-b-two", clock.GetUtcNow().AddMinutes(-1)),
        };
        using (PushOwner())
        await using (var seed = database.CreateContext())
        {
            await seed.Database.EnsureCreatedAsync(cancellationToken);
            seed.Jobs.AddRange(jobs);
            await seed.SaveChangesAsync(cancellationToken);
        }

        var executionLock = new InMemoryProjectExecutionLock();
        var services = new ServiceCollection();
        JobTestServices.AddRecoveryServices(services, database.Options, clock, executionLock);
        await using var provider = services.BuildServiceProvider();
        await provider
            .GetRequiredService<DatabaseInitializationStateStore>()
            .PersistAsync("test-password-hash", cancellationToken);
        var recovery = provider.GetRequiredService<InProcessJobRecovery>();
        await using var heldLease = await executionLock.AcquireAsync(projectA, cancellationToken);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Assert.False(UserInfoUtil.IsContextActive);
        var periodicRecovery = recovery.RunPeriodicAsync(stopping.Token);
        try
        {
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(30), cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(30));

            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var verificationUser = PushOwner();
                await using var verification = database.CreateContext();
                var statuses = await verification
                    .Jobs.AsNoTracking()
                    .Where(item => jobs.Select(job => job.Id).Contains(item.Id))
                    .ToDictionaryAsync(item => item.Id, item => item.Status, cancellationToken);
                if (statuses[jobs[1].Id] == JobStatus.Pending && statuses[jobs[2].Id] == JobStatus.Pending)
                {
                    Assert.Equal(JobStatus.Running, statuses[jobs[0].Id]);
                    Assert.Equal(2, await verification.JobLogs.CountAsync(cancellationToken));
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }

            Assert.Fail("The unlocked project was not recovered during the periodic scan.");
        }
        finally
        {
            stopping.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => periodicRecovery);
        }
    }

    private static IDisposable PushOwner() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
        );

    private static Job CreateRunningJob(Guid projectId, string name, DateTimeOffset startedAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Name = name,
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            NextRunTime = startedAt,
            Status = JobStatus.Running,
            IsEnabled = true,
            MaxRetryCount = 3,
            ActiveTurnId = Guid.CreateVersion7(),
            ActiveAttemptStartedAt = startedAt,
            CreateBy = "owner",
            CreateTime = startedAt,
        };
}
