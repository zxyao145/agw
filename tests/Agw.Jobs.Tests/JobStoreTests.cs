using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Tests;

public class JobStoreTests : IDisposable
{
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], authenticationType: "Test")
        )
    );

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task TryStartAttemptAsync_PersistsStableAttemptIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await CreateDatabaseAsync(cancellationToken);
        await using var connection = database.Connection;
        var options = database.Options;

        var jobId = Guid.CreateVersion7();
        var utcNow = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
        var job = CreateJob(jobId, Guid.CreateVersion7(), utcNow);

        await using (var dbContext = new AgwDbContext(options))
        {
            dbContext.Jobs.Add(job);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await using (var dbContext = new AgwDbContext(options))
        {
            var store = new JobRepo(dbContext, new TestTimeProvider(utcNow));
            var start = await store.TryStartAttemptAsync(ScheduledJob.FromJob(job), cancellationToken);
            var claim = Assert.IsType<JobAttemptStartResult.Claimed>(start).Claim;

            Assert.NotEqual(Guid.Empty, claim.TurnId);
            Assert.Equal(utcNow, claim.StartedAt);
            Assert.Equal(1, claim.Attempt);
        }

        await using var verifyContext = new AgwDbContext(options);
        var persistedJob = await verifyContext.Jobs.SingleAsync(cancellationToken);
        Assert.Equal(JobStatus.Running, persistedJob.Status);
        Assert.NotNull(persistedJob.ActiveTurnId);
        Assert.Equal(utcNow, persistedJob.ActiveAttemptStartedAt);
        var json = JsonSerializer.Serialize(persistedJob);
        Assert.DoesNotContain("activeTurnId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("activeAttemptStartedAt", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryStartAttemptAsync_ProjectChangedAfterPrefetch_RefreshesWithoutClaiming()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await CreateDatabaseAsync(cancellationToken);
        await using var connection = database.Connection;
        var utcNow = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
        var oldProjectId = Guid.CreateVersion7();
        var newProjectId = Guid.CreateVersion7();
        var job = CreateJob(Guid.CreateVersion7(), oldProjectId, utcNow);
        var scheduledJob = ScheduledJob.FromJob(job);

        await using (var seedContext = new AgwDbContext(database.Options))
        {
            seedContext.Jobs.Add(job);
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var updateContext = new AgwDbContext(database.Options))
        {
            var persisted = await updateContext.Jobs.SingleAsync(item => item.Id == job.Id, cancellationToken);
            persisted.ProjectId = newProjectId;
            await updateContext.SaveChangesAsync(cancellationToken);
        }

        await using (var claimContext = new AgwDbContext(database.Options))
        {
            var store = new JobRepo(claimContext, new TestTimeProvider(utcNow));
            var start = await store.TryStartAttemptAsync(scheduledJob, cancellationToken);
            var refresh = Assert.IsType<JobAttemptStartResult.Reschedule>(start);

            Assert.Equal(newProjectId, refresh.Job.ProjectId);
            Assert.Equal(utcNow, refresh.Job.NextRunTime);
        }

        await using var verifyContext = new AgwDbContext(database.Options);
        var persistedJob = await verifyContext.Jobs.SingleAsync(item => item.Id == job.Id, cancellationToken);
        Assert.Equal(JobStatus.Pending, persistedJob.Status);
        Assert.Null(persistedJob.ActiveTurnId);
        Assert.Null(persistedJob.ActiveAttemptStartedAt);
    }

    [Fact]
    public async Task TryStartAttemptAsync_NextRunTimeIsFuture_RefreshesWithoutClaiming()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await CreateDatabaseAsync(cancellationToken);
        await using var connection = database.Connection;
        var utcNow = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
        var job = CreateJob(Guid.CreateVersion7(), Guid.CreateVersion7(), utcNow.AddMinutes(1));
        var scheduledJob = ScheduledJob.FromJob(job) with { NextRunTime = utcNow };

        await using (var seedContext = new AgwDbContext(database.Options))
        {
            seedContext.Jobs.Add(job);
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var claimContext = new AgwDbContext(database.Options))
        {
            var store = new JobRepo(claimContext, new TestTimeProvider(utcNow));
            var start = await store.TryStartAttemptAsync(scheduledJob, cancellationToken);
            var refresh = Assert.IsType<JobAttemptStartResult.Reschedule>(start);

            Assert.Equal(job.NextRunTime, refresh.Job.NextRunTime);
        }

        await using var verifyContext = new AgwDbContext(database.Options);
        var persistedJob = await verifyContext.Jobs.SingleAsync(item => item.Id == job.Id, cancellationToken);
        Assert.Equal(JobStatus.Pending, persistedJob.Status);
        Assert.Null(persistedJob.ActiveTurnId);
    }

    private static async Task<(
        SqliteConnection Connection,
        DbContextOptions<AgwDbContext> Options
    )> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var setupContext = new AgwDbContext(options);
        await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        return (connection, options);
    }

    private static Job CreateJob(Guid jobId, Guid projectId, DateTimeOffset nextRunTime) =>
        new()
        {
            Id = jobId,
            ProjectId = projectId,
            AgentType = AgentRuntimeType.Agent,
            AgentId = Guid.CreateVersion7(),
            Name = "job",
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            NextRunTime = nextRunTime,
            Status = JobStatus.Pending,
            IsEnabled = true,
            CreateBy = "owner",
            CreateTime = nextRunTime,
            UpdateBy = "owner",
            UpdateTime = nextRunTime,
        };
}
