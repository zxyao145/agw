using System.Security.Claims;
using Agw.Agents.Contracts.Catalog;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Jobs.Application.Contracts;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Projects.Application;
using Agw.Projects.Application.Facades;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Tests;

public class JobAppServiceTests : IDisposable
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 15, 23, 30, 0, TimeSpan.Zero);
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-user")], authenticationType: "Test")
        )
    );

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task CreateAsync_BlankName_GeneratesCountBasedUtcName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(2, cancellationToken);

        var job = await fixture.Service.CreateAsync(
            CreateRequest("   "),
            "test-user",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("job-3-20260715", job.Name);
    }

    [Fact]
    public async Task CreateAsync_ProvidedName_TrimsAndPreservesName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(0, cancellationToken);

        var job = await fixture.Service.CreateAsync(
            CreateRequest("  Nightly Job  "),
            "test-user",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("Nightly Job", job.Name);
    }

    [Fact]
    public async Task UpdateAsync_BlankName_GeneratesCountBasedUtcName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(2, cancellationToken);

        var job = await fixture.Service.UpdateAsync(
            fixture.FirstJobId,
            new JobUpdateRequest
            {
                ProjectId = Guid.CreateVersion7(),
                Name = "",
                TriggerType = TriggerType.Interval,
                TriggerValue = "00:01:00",
                MaxRetryCount = 3,
                IsEnabled = true,
                Status = JobStatus.Pending,
            },
            "test-user",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.NotNull(job);
        Assert.Equal("job-3-20260715", job!.Name);
    }

    [Fact]
    public async Task UpdateEnabledAsync_Disable_ChangesOnlyEnabledAndAuditFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(1, cancellationToken);
        var original = await fixture.GetJobAsync(fixture.FirstJobId, cancellationToken);
        var updatedAt = UtcNow.AddMinutes(5);
        fixture.TimeProvider.SetUtcNow(updatedAt);

        var job = await fixture.Service.UpdateEnabledAsync(
            new JobEnabledUpdateRequest { JobId = fixture.FirstJobId, IsEnabled = false },
            "test-user"
        );

        Assert.NotNull(job);
        Assert.False(job!.IsEnabled);
        Assert.Equal("test-user", job.UpdateBy);
        Assert.Equal(updatedAt, job.UpdateTime);
        Assert.Equal(original.ProjectId, job.ProjectId);
        Assert.Equal(original.Name, job.Name);
        Assert.Equal(original.TriggerType, job.TriggerType);
        Assert.Equal(original.TriggerValue, job.TriggerValue);
        Assert.Equal(original.NextRunTime, job.NextRunTime);
        Assert.Equal(original.Status, job.Status);
        Assert.Equal(original.RetryCount, job.RetryCount);
        Assert.Equal(original.MaxRetryCount, job.MaxRetryCount);
    }

    [Fact]
    public async Task UpdateEnabledAsync_MissingJob_ReturnsNull()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(0, cancellationToken);

        var job = await fixture.Service.UpdateEnabledAsync(
            new JobEnabledUpdateRequest { JobId = Guid.CreateVersion7(), IsEnabled = false },
            "test-user"
        );

        Assert.Null(job);
    }

    [Fact]
    public async Task UpdateEnabledAsync_Enable_WakesScheduler()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(1, cancellationToken);
        var wait = fixture.SchedulerWakeSignal.WaitAsync(cancellationToken);

        await fixture.Service.UpdateEnabledAsync(
            new JobEnabledUpdateRequest { JobId = fixture.FirstJobId, IsEnabled = true },
            "test-user"
        );

        await wait.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
    }

    [Fact]
    public async Task UpdateAsync_ActiveAttempt_ThrowsConflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(1, cancellationToken);
        await fixture.SetActiveAttemptAsync(fixture.FirstJobId, cancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.UpdateAsync(
                fixture.FirstJobId,
                CreateUpdateRequest(),
                "test-user",
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.JobActiveAttemptConflict.Code, exception.Code);
    }

    [Fact]
    public async Task DeleteAsync_ActiveAttempt_ThrowsConflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(1, cancellationToken);
        await fixture.SetActiveAttemptAsync(fixture.FirstJobId, cancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() => fixture.Service.DeleteAsync(fixture.FirstJobId));

        Assert.Equal(ErrorCodes.JobActiveAttemptConflict.Code, exception.Code);
    }

    private static JobCreateRequest CreateRequest(string name)
    {
        return new JobCreateRequest
        {
            ProjectId = Guid.CreateVersion7(),
            Name = name,
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:01:00",
            MaxRetryCount = 3,
            IsEnabled = true,
        };
    }

    private static JobUpdateRequest CreateUpdateRequest() =>
        new()
        {
            ProjectId = Guid.CreateVersion7(),
            Name = "updated",
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:01:00",
            MaxRetryCount = 3,
            IsEnabled = true,
            Status = JobStatus.Pending,
        };

    private sealed class JobAppServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly AgwDbContext _dbContext;

        private JobAppServiceFixture(
            SqliteConnection connection,
            AgwDbContext dbContext,
            JobAppService service,
            TestTimeProvider timeProvider,
            JobSchedulerWakeSignal schedulerWakeSignal,
            Guid firstJobId
        )
        {
            _connection = connection;
            _dbContext = dbContext;
            Service = service;
            TimeProvider = timeProvider;
            SchedulerWakeSignal = schedulerWakeSignal;
            FirstJobId = firstJobId;
        }

        public JobAppService Service { get; }
        public TestTimeProvider TimeProvider { get; }
        public JobSchedulerWakeSignal SchedulerWakeSignal { get; }
        public Guid FirstJobId { get; }

        public async Task<Job> GetJobAsync(Guid id, CancellationToken cancellationToken)
        {
            _dbContext.ChangeTracker.Clear();
            return await _dbContext.Jobs.AsNoTracking().SingleAsync(job => job.Id == id, cancellationToken);
        }

        public async Task SetActiveAttemptAsync(Guid id, CancellationToken cancellationToken)
        {
            var job = await _dbContext.Jobs.SingleAsync(item => item.Id == id, cancellationToken);
            job.Status = JobStatus.Running;
            job.ActiveExecutionId = Guid.CreateVersion7();
            job.ActiveAttemptStartedAt = UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        public static async Task<JobAppServiceFixture> CreateAsync(int jobCount, CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);

            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var dbContext = new AgwDbContext(options);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);

            var jobs = Enumerable
                .Range(1, jobCount)
                .Select(index => new Job
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = Guid.CreateVersion7(),
                    Name = $"Existing Job {index}",
                    TriggerType = TriggerType.Interval,
                    TriggerValue = "00:01:00",
                    NextRunTime = UtcNow.AddMinutes(index),
                    MaxRetryCount = 3,
                    IsEnabled = true,
                    Status = JobStatus.Pending,
                    CreateBy = "test-user",
                    CreateTime = UtcNow,
                    UpdateBy = "test-user",
                    UpdateTime = UtcNow,
                })
                .ToList();

            if (jobs.Count > 0)
            {
                await dbContext.Jobs.AddRangeAsync(jobs, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var timeProvider = new TestTimeProvider(UtcNow);
            var schedulerWakeSignal = new JobSchedulerWakeSignal(timeProvider);
            var userInfo = new TestUserInfoService();
            var projectResolver = new ProjectResolver(dbContext, userInfo);
            var taskService = new TaskExecutionAppService(dbContext, projectResolver, timeProvider, userInfo);
            var taskResolver = new TaskAppService(dbContext, projectResolver, taskService, userInfo);
            var service = new JobAppService(
                dbContext,
                new ProjectTaskFacade(taskService, dbContext, taskResolver, userInfo),
                new JobScheduleCalculator(),
                schedulerWakeSignal,
                timeProvider,
                userInfo,
                new TestProjectRuntimeFacade(),
                new TestAgentCatalogFacade()
            );

            return new JobAppServiceFixture(
                connection,
                dbContext,
                service,
                timeProvider,
                schedulerWakeSignal,
                jobs.FirstOrDefault()?.Id ?? Guid.Empty
            );
        }

        public async ValueTask DisposeAsync()
        {
            await _dbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestProjectRuntimeFacade : IProjectRuntimeFacade
    {
        public Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(
            Guid projectId,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult<ProjectRuntimeSnapshot?>(
                new ProjectRuntimeSnapshot(
                    projectId,
                    "project",
                    null,
                    null,
                    [],
                    new Dictionary<string, string>(),
                    [],
                    [],
                    []
                )
            );

        public Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class TestAgentCatalogFacade : IAgentCatalogFacade
    {
        public Task<IReadOnlyList<AgentDescriptor>> ListDiscoverableAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<AgentDescriptor>>([]);

        public Task<AgentDescriptor?> FindDiscoverableByNameAsync(
            string name,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<AgentDescriptor?>(null);

        public Task<IReadOnlySet<Guid>> FilterExistingMcpServerIdsAsync(
            IReadOnlyCollection<Guid> serverIds,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlySet<Guid>>(serverIds.ToHashSet());

        public Task<bool> IsOwnedTargetAsync(
            AgentRuntimeType type,
            Guid id,
            string ownerUserId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(true);

        public Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentCatalogMetrics(0, 0));
    }
}
