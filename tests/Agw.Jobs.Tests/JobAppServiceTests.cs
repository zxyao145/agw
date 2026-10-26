using System.Security.Claims;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Definitions.Facades;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Jobs;
using Agw.Infrastructure.Projects;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Application.Facades;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Contracts;
using Agw.Jobs.Domain.Services;
using Agw.Jobs.Infrastructure;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Projects.Application;
using Agw.Projects.Application.Facades;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Runtime;
using Agw.Projects.Domain.Services;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Skills.Application.Facades;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task ListLogsAsync_RunWithOwnedConversation_ReturnsConversationId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(1, cancellationToken);
        var conversationId = await fixture.AddLogWithConversationAsync(
            fixture.FirstJobId,
            "test-user",
            UtcNow.AddMinutes(2),
            cancellationToken
        );
        await fixture.AddLogWithConversationAsync(
            fixture.FirstJobId,
            "other-user",
            UtcNow.AddMinutes(1),
            cancellationToken
        );
        await fixture.AddLogWithoutConversationAsync(fixture.FirstJobId, UtcNow, cancellationToken);

        var logs = await fixture.Service.ListLogsAsync(fixture.FirstJobId, cancellationToken);

        Assert.Equal(3, logs.Count);
        Assert.Equal(conversationId, logs[0].ConversationId);
        Assert.Null(logs[1].ConversationId);
        Assert.Null(logs[2].ConversationId);
    }

    [Fact]
    public async Task DeleteAsync_ActiveAttempt_ThrowsConflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(1, cancellationToken);
        await fixture.SetActiveAttemptAsync(fixture.FirstJobId, cancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.DeleteAsync(fixture.FirstJobId, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.JobActiveAttemptConflict.Code, exception.Code);
    }

    [Fact]
    public async Task DeleteByProjectAsync_ActiveAttempt_ThrowsConflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(1, cancellationToken);
        await fixture.SetActiveAttemptAsync(fixture.FirstJobId, cancellationToken);
        var job = await fixture.GetJobAsync(fixture.FirstJobId, cancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.DeleteByProjectAsync(fixture.FirstJobId, job.ProjectId, cancellationToken)
        );

        Assert.Equal(ErrorCodes.JobActiveAttemptConflict.Code, exception.Code);
    }

    [Fact]
    public async Task DeleteAsync_JobDeleteFails_PreservesJobLogs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(1, cancellationToken);
        await fixture.AddLogWithoutConversationAsync(fixture.FirstJobId, UtcNow, cancellationToken);
        var job = await fixture.GetJobAsync(fixture.FirstJobId, cancellationToken);
        await fixture.RejectJobDeleteAsync(cancellationToken);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Service.DeleteAsync(job.Id, TestContext.Current.CancellationToken)
        );
        var sqliteException = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Contains("job delete rejected", sqliteException.Message, StringComparison.Ordinal);

        Assert.NotNull(await fixture.GetJobAsync(job.Id, cancellationToken));
        Assert.Equal(1, await fixture.CountLogsAsync(job.Id, cancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Postgres")]
    public async Task Delete_ConcurrentAttemptClaim_PreservesJobLogs(bool deleteByProject)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adminConnectionString = PostgresJobTestDatabase.RequireAdminConnectionString();

        var deleteGate = new JobLogDeleteGate();
        await using var fixture = await PostgresJobAppServiceFixture.CreateAsync(
            adminConnectionString,
            deleteGate,
            cancellationToken
        );
        Task deletion;
        if (deleteByProject)
        {
            deletion = DeleteByProjectAsync(fixture);
        }
        else
        {
            deletion = fixture.Service.DeleteAsync(fixture.JobId, TestContext.Current.CancellationToken);
        }
        JobAttemptClaim claim;
        try
        {
            await deleteGate.DeleteExecuted.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await using var claimContext = fixture.Database.CreateContext();
            var currentJob = await claimContext.Jobs.SingleAsync(job => job.Id == fixture.JobId, cancellationToken);
            var start = await new JobRepo(claimContext, fixture.TimeProvider).TryStartAttemptAsync(
                ScheduledJob.FromJob(currentJob),
                cancellationToken
            );
            claim = Assert.IsType<JobAttemptStartResult.Claimed>(start).Claim;
        }
        finally
        {
            deleteGate.Continue();
        }

        var exception = await Assert.ThrowsAsync<AgwException>(() => deletion);
        Assert.Equal(ErrorCodes.JobActiveAttemptConflict.Code, exception.Code);

        await using var verificationContext = fixture.Database.CreateContext();
        var persistedJob = await verificationContext.Jobs.SingleAsync(
            job => job.Id == fixture.JobId,
            cancellationToken
        );
        var logs = await verificationContext
            .JobLogs.Where(log => log.JobId == fixture.JobId)
            .ToListAsync(cancellationToken);
        Assert.Equal(JobStatus.Running, persistedJob.Status);
        Assert.Equal(claim.TurnId, persistedJob.ActiveTurnId);
        Assert.Single(logs);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Delete_ConcurrentMutableUpdate_ReturnsModificationConflictAndPreservesJobLogs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adminConnectionString = PostgresJobTestDatabase.RequireAdminConnectionString();

        var deleteGate = new JobLogDeleteGate();
        await using var fixture = await PostgresJobAppServiceFixture.CreateAsync(
            adminConnectionString,
            deleteGate,
            cancellationToken
        );
        var deletion = fixture.Service.DeleteAsync(fixture.JobId, cancellationToken);
        try
        {
            await deleteGate.DeleteExecuted.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await using var updateContext = fixture.Database.CreateContext();
            var job = await updateContext.Jobs.SingleAsync(item => item.Id == fixture.JobId, cancellationToken);
            job.IsEnabled = false;
            job.UpdateTime = UtcNow.AddMinutes(1);
            await updateContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            deleteGate.Continue();
        }

        var exception = await Assert.ThrowsAsync<AgwException>(() => deletion);
        Assert.Equal(ErrorCodes.JobModificationConflict.Code, exception.Code);

        await using var verificationContext = fixture.Database.CreateContext();
        var persistedJob = await verificationContext.Jobs.SingleAsync(
            item => item.Id == fixture.JobId,
            cancellationToken
        );
        var logs = await verificationContext
            .JobLogs.Where(log => log.JobId == fixture.JobId)
            .ToListAsync(cancellationToken);
        Assert.False(persistedJob.IsEnabled);
        Assert.Single(logs);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Delete_ConcurrentProjectDeletion_ReturnsSameMissingResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adminConnectionString = PostgresJobTestDatabase.RequireAdminConnectionString();

        var deleteGate = new JobLogDeleteGate();
        await using var fixture = await PostgresJobAppServiceFixture.CreateAsync(
            adminConnectionString,
            deleteGate,
            cancellationToken,
            includeLog: false
        );
        var deletion = fixture.Service.DeleteAsync(fixture.JobId, cancellationToken);
        try
        {
            await deleteGate.DeleteExecuted.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await using var projectContext = fixture.Database.CreateContext();
            var scopeMaintenance = new DurableExecutionScopeMaintenance(
                projectContext,
                fixture.TimeProvider,
                NullLogger<DurableExecutionScopeMaintenance>.Instance
            );
            var projectDeletion = new ProjectDeletionCoordinator(
                projectContext,
                InMemoryApplicationLock.Shared,
                scopeMaintenance,
                fixture.TimeProvider
            );
            var deleted = await projectDeletion
                .DeleteProjectAsync(new ProjectDeletionTarget(fixture.ProjectId, "test-user"), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.True(deleted);
        }
        finally
        {
            deleteGate.Continue();
        }

        Assert.False(await deletion);
        Assert.False(await fixture.Service.DeleteAsync(fixture.JobId, cancellationToken));
    }

    private static Task<Job?> DeleteByProjectAsync(PostgresJobAppServiceFixture fixture) =>
        fixture.Service.DeleteByProjectAsync(fixture.JobId, fixture.ProjectId);

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

        public async Task<Guid> AddLogWithConversationAsync(
            Guid jobId,
            string conversationOwner,
            DateTimeOffset startTime,
            CancellationToken cancellationToken
        )
        {
            var projectId = Guid.CreateVersion7();
            var conversationId = Guid.CreateVersion7();
            var taskId = Guid.CreateVersion7();
            _dbContext.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = $"project-{projectId:N}",
                    Type = ProjectType.UserDefined,
                    CreateBy = conversationOwner,
                    CreateTime = UtcNow,
                }
            );
            _dbContext.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = conversationId,
                    ProjectId = projectId,
                    ContextId = Guid.CreateVersion7().ToString("D"),
                    CreateBy = conversationOwner,
                    CreateTime = UtcNow,
                }
            );
            _dbContext.ProjectConversationChatHistories.Add(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = conversationId,
                    TaskId = taskId,
                    JobId = jobId,
                    Status = TaskExecutionStatus.Succeeded,
                    ConversationSequence = 0,
                    CreateTime = UtcNow,
                }
            );
            AddLog(jobId, taskId, startTime);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return conversationId;
        }

        public async Task AddLogWithoutConversationAsync(
            Guid jobId,
            DateTimeOffset startTime,
            CancellationToken cancellationToken
        )
        {
            AddLog(jobId, Guid.CreateVersion7(), startTime);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        public Task<int> CountLogsAsync(Guid jobId, CancellationToken cancellationToken) =>
            _dbContext.JobLogs.CountAsync(log => log.JobId == jobId, cancellationToken);

        public Task RejectJobDeleteAsync(CancellationToken cancellationToken) =>
            _dbContext.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_job_delete BEFORE DELETE ON job BEGIN SELECT RAISE(ABORT, 'job delete rejected'); END;",
                cancellationToken
            );

        private void AddLog(Guid jobId, Guid taskId, DateTimeOffset startTime) =>
            _dbContext.JobLogs.Add(
                new JobLog
                {
                    Id = Guid.CreateVersion7(),
                    JobId = jobId,
                    TaskId = taskId,
                    StartTime = startTime,
                    EndTime = startTime.AddSeconds(5),
                    Success = true,
                    Attempt = 1,
                }
            );

        public async Task SetActiveAttemptAsync(Guid id, CancellationToken cancellationToken)
        {
            var job = await _dbContext.Jobs.SingleAsync(item => item.Id == id, cancellationToken);
            job.Status = JobStatus.Running;
            job.ActiveTurnId = Guid.CreateVersion7();
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
            var taskService = new TaskExecutionAppService(
                dbContext,
                projectResolver,
                new ConversationHistoryDomainService(),
                timeProvider,
                userInfo,
                new ConversationCreatorStore(dbContext)
            );
            var taskResolver = new TaskAppService(dbContext, projectResolver, taskService, userInfo);
            var service = new JobAppService(
                dbContext,
                new ProjectTaskFacade(taskService, dbContext, taskResolver, userInfo),
                new JobDefinitionDomainService(
                    new JobRepository(dbContext),
                    new TestProjectRuntimeFacade(),
                    new TestAgentCatalogFacade()
                ),
                schedulerWakeSignal,
                timeProvider,
                userInfo,
                new JobDeletionCoordinator(
                    dbContext,
                    InMemoryApplicationLock.Shared,
                    userInfo,
                    NullLogger<JobDeletionCoordinator>.Instance
                )
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

    private sealed class PostgresJobAppServiceFixture : IAsyncDisposable
    {
        private readonly AgwDbContext _dbContext;

        private PostgresJobAppServiceFixture(
            PostgresJobTestDatabase database,
            AgwDbContext dbContext,
            JobAppService service,
            TestTimeProvider timeProvider,
            Guid jobId,
            Guid projectId
        )
        {
            Database = database;
            _dbContext = dbContext;
            Service = service;
            TimeProvider = timeProvider;
            JobId = jobId;
            ProjectId = projectId;
        }

        public PostgresJobTestDatabase Database { get; }
        public JobAppService Service { get; }
        public TestTimeProvider TimeProvider { get; }
        public Guid JobId { get; }
        public Guid ProjectId { get; }

        public static async Task<PostgresJobAppServiceFixture> CreateAsync(
            string adminConnectionString,
            JobLogDeleteGate deleteGate,
            CancellationToken cancellationToken,
            bool includeLog = true
        )
        {
            var database = await PostgresJobTestDatabase.CreateAsync(
                adminConnectionString,
                cancellationToken,
                deleteGate
            );
            var dbContext = database.CreateContext();
            var jobId = Guid.CreateVersion7();
            var now = UtcNow;
            var job = new Job
            {
                Id = jobId,
                ProjectId = Guid.CreateVersion7(),
                Name = "concurrent-delete",
                TriggerType = TriggerType.Interval,
                TriggerValue = "00:01:00",
                NextRunTime = now,
                Status = JobStatus.Pending,
                IsEnabled = true,
                CreateBy = "test-user",
                CreateTime = now,
                UpdateBy = "test-user",
                UpdateTime = now,
            };
            dbContext.Projects.Add(
                new Project
                {
                    Id = job.ProjectId,
                    Name = "concurrent-delete-project",
                    Type = ProjectType.UserDefined,
                    CreateBy = "test-user",
                    CreateTime = now,
                }
            );
            dbContext.Jobs.Add(job);
            if (includeLog)
            {
                dbContext.JobLogs.Add(
                    new JobLog
                    {
                        Id = Guid.CreateVersion7(),
                        JobId = jobId,
                        TaskId = Guid.CreateVersion7(),
                        StartTime = now,
                        EndTime = now.AddMinutes(1),
                        Success = true,
                        Attempt = 1,
                        CreateBy = "test-user",
                        CreateTime = now,
                        UpdateBy = "test-user",
                        UpdateTime = now,
                    }
                );
            }
            await dbContext.SaveChangesAsync(cancellationToken);

            var timeProvider = new TestTimeProvider(now);
            var userInfo = new UserInfoService();
            var projectResolver = new ProjectResolver(dbContext, userInfo);
            var taskExecution = new TaskExecutionAppService(
                dbContext,
                projectResolver,
                new ConversationHistoryDomainService(),
                timeProvider,
                userInfo,
                new ConversationCreatorStore(dbContext)
            );
            var taskAppService = new TaskAppService(dbContext, projectResolver, taskExecution, userInfo);
            var catalog = new AgentCatalogFacade(dbContext, userInfo);
            var projectResourceBindings = new ProjectResourceBindingDomainService(
                catalog,
                new SkillReferenceFacade(dbContext, userInfo),
                new ConnectionReferenceFacade(dbContext, userInfo)
            );
            var projectAppService = new ProjectAppService(
                dbContext,
                projectResourceBindings,
                new ProjectDeletionCoordinator(
                    dbContext,
                    InMemoryApplicationLock.Shared,
                    new DurableExecutionScopeMaintenance(
                        dbContext,
                        timeProvider,
                        NullLogger<DurableExecutionScopeMaintenance>.Instance
                    ),
                    timeProvider
                ),
                projectResolver,
                userInfo
            );
            var service = new JobAppService(
                dbContext,
                new ProjectTaskFacade(taskExecution, dbContext, taskAppService, userInfo),
                new JobDefinitionDomainService(
                    new JobRepository(dbContext),
                    new ProjectRuntimeFacade(projectAppService),
                    catalog
                ),
                new JobSchedulerWakeSignal(timeProvider),
                timeProvider,
                userInfo,
                new JobDeletionCoordinator(
                    dbContext,
                    InMemoryApplicationLock.Shared,
                    userInfo,
                    NullLogger<JobDeletionCoordinator>.Instance
                )
            );
            dbContext.ChangeTracker.Clear();
            return new PostgresJobAppServiceFixture(database, dbContext, service, timeProvider, jobId, job.ProjectId);
        }

        public async ValueTask DisposeAsync()
        {
            await _dbContext.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed class JobLogDeleteGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _deleteExecuted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeleteExecuted => _deleteExecuted.Task;

        public void Continue() => _continue.TrySetResult();

        public override async ValueTask<int> NonQueryExecutedAsync(
            System.Data.Common.DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        )
        {
            if (
                command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("job_log", StringComparison.OrdinalIgnoreCase)
                && _deleteExecuted.TrySetResult()
            )
            {
                await _continue.Task.WaitAsync(cancellationToken);
            }

            return result;
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
