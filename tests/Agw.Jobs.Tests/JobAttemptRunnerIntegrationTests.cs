using System.Security.Claims;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Jobs;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs.Tests;

public sealed class JobAttemptRunnerIntegrationTests
{
    [Fact]
    public async Task RunAsync_InvalidAgentTarget_PersistsRetryAndFailureLog()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var startedAt = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(startedAt);
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            AgentType = AgentRuntimeType.Agent,
            Name = "missing-agent-target",
            Prompt = "run",
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            NextRunTime = startedAt,
            Status = JobStatus.Pending,
            IsEnabled = true,
            MaxRetryCount = 2,
            CreateBy = "owner",
            CreateTime = startedAt,
            UpdateBy = "owner",
            UpdateTime = startedAt,
        };
        using (
            UserInfoUtil.Push(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
            )
        )
        {
            await using var seed = database.CreateContext();
            await seed.Database.EnsureCreatedAsync(cancellationToken);
            seed.Jobs.Add(job);
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var services = JobTestServices.Create(
            database.Options,
            timeProvider,
            new InMemoryProjectExecutionLock()
        );
        await using var scope = services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<JobAttemptRunner>();
        var result = await runner.RunAsync(ScheduledJob.FromJob(job), cancellationToken);

        var reschedule = Assert.IsType<JobAttemptResult.Reschedule>(result);
        Assert.Equal(startedAt.Add(JobSchedulingDefaults.RetryDelay), reschedule.Job.NextRunTime);
        Assert.Equal(1, reschedule.Job.RetryCount);

        using (UserInfoUtil.PushSystemScope())
        await using (var verification = database.CreateContext())
        {
            var persistedJob = await verification.Jobs.SingleAsync(item => item.Id == job.Id, cancellationToken);
            var log = await verification.JobLogs.SingleAsync(item => item.JobId == job.Id, cancellationToken);
            Assert.Equal(JobStatus.Pending, persistedJob.Status);
            Assert.Equal(1, persistedJob.RetryCount);
            Assert.Equal(startedAt.Add(JobSchedulingDefaults.RetryDelay), persistedJob.NextRunTime);
            Assert.Null(persistedJob.ActiveTurnId);
            Assert.False(log.Success);
            Assert.Equal(1, log.Attempt);
            Assert.NotNull(log.ErrorMessage);
        }
    }

    [Fact]
    public async Task RunAsync_CompletedDurableExecution_PersistsSuccessfulAttempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var startedAt = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(startedAt);
        var projectId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            AgentType = AgentRuntimeType.Agent,
            AgentId = agentId,
            Name = "successful-job",
            Prompt = "run",
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            NextRunTime = startedAt,
            Status = JobStatus.Pending,
            IsEnabled = true,
            MaxRetryCount = 2,
            CreateBy = "owner",
            CreateTime = startedAt,
            UpdateBy = "owner",
            UpdateTime = startedAt,
        };
        using (PushOwner())
        {
            await using var seed = database.CreateContext();
            await seed.Database.EnsureCreatedAsync(cancellationToken);
            seed.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "scheduled-job-project",
                    CreateBy = "owner",
                    CreateTime = startedAt,
                    UpdateBy = "owner",
                    UpdateTime = startedAt,
                }
            );
            seed.Agents.Add(
                new Agent
                {
                    Id = agentId,
                    Name = "scheduled-job-agent",
                    DisplayName = "Scheduled job agent",
                    CreateBy = "owner",
                    CreateTime = startedAt,
                    UpdateBy = "owner",
                    UpdateTime = startedAt,
                }
            );
            seed.Jobs.Add(job);
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var services = JobTestServices.Create(
            database.Options,
            timeProvider,
            new InMemoryProjectExecutionLock()
        );
        await using var scope = services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<JobAttemptRunner>();
        var attempt = runner.RunAsync(ScheduledJob.FromJob(job), cancellationToken);
        var turnId = await WaitForDurableTurnAsync(database, job.Id, attempt, cancellationToken);

        await using var workerScope = services.CreateAsyncScope();
        var workerServices = workerScope.ServiceProvider;
        using (PushOwner())
        {
            var leases = workerServices.GetRequiredService<IDurableExecutionLeases>();
            var lease = await leases.TryClaimAsync(
                turnId,
                "jobs-test-worker",
                TimeSpan.FromMinutes(1),
                cancellationToken
            );
            Assert.NotNull(lease);
            var snapshot = await workerServices
                .GetRequiredService<DurableExecutionStore>()
                .LoadClaimedAsync(turnId, cancellationToken);
            Assert.NotNull(snapshot);

            using var ownership = new CancellationTokenSource();
            await workerServices
                .GetRequiredService<DurableExecutionCoordinator>()
                .CommitResultAsync(
                    snapshot,
                    lease,
                    leases.CreateGuard(lease, ownership),
                    new DurableExecutionSegmentResult
                    {
                        TurnId = turnId,
                        SegmentIndex = snapshot.SegmentIndex,
                        Status = DurableExecutionSegmentStatus.Completed,
                        StepCount = 1,
                    },
                    failureReported: true,
                    cancellationToken
                );
        }

        var result = Assert.IsType<JobAttemptResult.Reschedule>(
            await attempt.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
        );
        Assert.Equal(startedAt.AddMinutes(15), result.Job.NextRunTime);

        using (UserInfoUtil.PushSystemScope())
        await using (var verification = database.CreateContext())
        {
            var persistedJob = await verification.Jobs.SingleAsync(item => item.Id == job.Id, cancellationToken);
            var log = await verification.JobLogs.SingleAsync(item => item.JobId == job.Id, cancellationToken);
            var tasks = await verification
                .ProjectConversationChatHistories.Where(item => item.TaskId == turnId)
                .ToListAsync(cancellationToken);
            Assert.Equal(JobStatus.Pending, persistedJob.Status);
            Assert.Equal(0, persistedJob.RetryCount);
            Assert.Equal(startedAt.AddMinutes(15), persistedJob.NextRunTime);
            Assert.Null(persistedJob.ActiveTurnId);
            Assert.True(log.Success);
            Assert.Equal(1, log.Attempt);
            Assert.Null(log.ErrorMessage);
            Assert.NotEmpty(tasks);
            Assert.All(tasks, task => Assert.Equal(TaskExecutionStatus.Succeeded, task.Status));
        }
    }

    [Fact]
    public async Task RunAsync_MissingJob_DropsScheduledEntry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var startedAt = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqliteJobTestDatabase.CreateAsync();
        await using (var seed = database.CreateContext())
        {
            await seed.Database.EnsureCreatedAsync(cancellationToken);
        }
        await using var services = JobTestServices.Create(
            database.Options,
            new TestTimeProvider(startedAt),
            new InMemoryProjectExecutionLock()
        );
        await using var scope = services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<JobAttemptRunner>();
        var missingJob = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            Name = "deleted-job",
            TriggerType = TriggerType.Once,
            TriggerValue = string.Empty,
            NextRunTime = startedAt,
            Status = JobStatus.Pending,
            IsEnabled = true,
            CreateBy = "owner",
        };

        var result = await runner.RunAsync(ScheduledJob.FromJob(missingJob), cancellationToken);

        Assert.IsType<JobAttemptResult.Drop>(result);
    }

    private static async Task<Guid> WaitForDurableTurnAsync(
        SqliteJobTestDatabase database,
        Guid jobId,
        Task<JobAttemptResult> attempt,
        CancellationToken cancellationToken
    )
    {
        for (var index = 0; index < 500; index++)
        {
            using (UserInfoUtil.PushSystemScope())
            await using (var verification = database.CreateContext())
            {
                var turnId = await verification
                    .Jobs.AsNoTracking()
                    .Where(item => item.Id == jobId)
                    .Select(item => item.ActiveTurnId)
                    .SingleAsync(cancellationToken);
                if (
                    turnId is Guid activeTurnId
                    && await verification.DurableExecutions.AnyAsync(item => item.Id == activeTurnId, cancellationToken)
                )
                {
                    return activeTurnId;
                }
            }

            if (attempt.IsCompleted)
            {
                await attempt;
                Assert.Fail("The Job attempt completed before durable execution registration.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        throw new TimeoutException("The durable Job execution was not registered.");
    }

    private static IDisposable PushOwner() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
        );
}
