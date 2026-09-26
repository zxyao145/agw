using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Jobs;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Projects.Application;
using Agw.Projects.Application.Facades;
using Agw.Projects.Domain.Services;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Jobs.Tests;

public sealed class JobAttemptOutcomeRecorderTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FinishedAt = StartedAt.AddMinutes(2);

    [Fact]
    public async Task RecordAsync_LogSaveFails_RollsBackProjectTaskAndJob()
    {
        var token = TestContext.Current.CancellationToken;
        using var owner = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
        );
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        var failure = new FailOutcomeCommit();
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(failure)
            .Options;
        await using var db = new AgwDbContext(options);
        await db.Database.EnsureCreatedAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var executionId = Guid.CreateVersion7();
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Name = "atomic-outcome",
            Status = JobStatus.Running,
            IsEnabled = true,
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            ActiveExecutionId = executionId,
            ActiveAttemptStartedAt = StartedAt,
            CreateBy = "owner",
        };
        db.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = "project",
                CreateBy = "owner",
            }
        );
        db.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = conversationId,
                ProjectId = projectId,
                ContextId = "context",
                CreateBy = "owner",
            }
        );
        db.ProjectConversationChatHistories.Add(
            new ProjectConversationChatHistory
            {
                Id = Guid.CreateVersion7(),
                ConversationId = conversationId,
                TaskId = executionId,
                Status = TaskExecutionStatus.Running,
            }
        );
        db.Jobs.Add(job);
        await db.SaveChangesAsync(token);
        var user = new TestUserInfoService("owner");
        var projects = new ProjectResolver(db, user);
        var tasks = new TaskExecutionAppService(
            db,
            projects,
            new ConversationHistoryDomainService(),
            new TestTimeProvider(FinishedAt),
            user
        );
        var facade = new ProjectTaskFacade(tasks, db, new TaskAppService(db, projects, tasks, user), user);
        var recorder = new JobAttemptOutcomeRecorder(
            db,
            new JobOutcomeTransaction(db, InMemoryApplicationLock.Shared),
            facade,
            new TestTimeProvider(FinishedAt)
        );

        await Assert.ThrowsAsync<IOException>(() => recorder.RecordAsync(job.Id, executionId, true, null, token));
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(
            TaskExecutionStatus.Running,
            (await db.ProjectConversationChatHistories.SingleAsync(token)).Status
        );
        Assert.Equal(JobStatus.Running, (await db.Jobs.SingleAsync(token)).Status);
        Assert.Empty(await db.JobLogs.ToListAsync(token));

        failure.Enabled = false;
        await recorder.RecordAsync(job.Id, executionId, true, null, token);
        await recorder.RecordAsync(job.Id, executionId, true, null, token);
        db.ChangeTracker.Clear();
        Assert.Equal(
            TaskExecutionStatus.Succeeded,
            (await db.ProjectConversationChatHistories.SingleAsync(token)).Status
        );
        Assert.Null((await db.Jobs.SingleAsync(token)).ActiveExecutionId);
        Assert.Single(await db.JobLogs.ToListAsync(token));
    }

    private sealed class FailOutcomeCommit : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<JobLog>().Any(e => e.State == EntityState.Added))
                throw new IOException("Injected outcome commit failure");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task RecordAsync_FailedAttempt_IsAtomicAndIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var executionId = Guid.CreateVersion7();
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            AgentType = AgentRuntimeType.Agent,
            AgentId = Guid.CreateVersion7(),
            Name = "durable job",
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            NextRunTime = StartedAt,
            Status = JobStatus.Running,
            IsEnabled = true,
            MaxRetryCount = 3,
            ActiveExecutionId = executionId,
            ActiveAttemptStartedAt = StartedAt,
            CreateBy = "owner",
            CreateTime = StartedAt,
            UpdateBy = "scheduler",
            UpdateTime = StartedAt,
        };
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);

        var userInfo = new TestUserInfoService("owner");
        var projectResolver = new ProjectResolver(dbContext, userInfo);
        var taskExecution = new TaskExecutionAppService(
            dbContext,
            projectResolver,
            new ConversationHistoryDomainService(),
            new TestTimeProvider(FinishedAt),
            userInfo
        );
        var taskResolver = new TaskAppService(dbContext, projectResolver, taskExecution, userInfo);
        var recorder = new JobAttemptOutcomeRecorder(
            dbContext,
            new JobOutcomeTransaction(dbContext, InMemoryApplicationLock.Shared),
            new ProjectTaskFacade(taskExecution, dbContext, taskResolver, userInfo),
            new TestTimeProvider(FinishedAt)
        );

        var first = await recorder.RecordAsync(
            job.Id,
            executionId,
            success: false,
            errorMessage: "boom",
            cancellationToken: cancellationToken
        );
        var second = await recorder.RecordAsync(
            job.Id,
            executionId,
            success: false,
            errorMessage: "boom",
            cancellationToken: cancellationToken
        );

        Assert.IsType<JobAttemptResult.Reschedule>(first);
        Assert.IsType<JobAttemptResult.Drop>(second);
        dbContext.ChangeTracker.Clear();
        var persistedJob = await dbContext.Jobs.SingleAsync(cancellationToken);
        var log = await dbContext.JobLogs.SingleAsync(cancellationToken);
        Assert.Equal(JobStatus.Pending, persistedJob.Status);
        Assert.Equal(1, persistedJob.RetryCount);
        Assert.Equal(FinishedAt.Add(JobSchedulingDefaults.RetryDelay), persistedJob.NextRunTime);
        Assert.Null(persistedJob.ActiveExecutionId);
        Assert.Null(persistedJob.ActiveAttemptStartedAt);
        Assert.Equal(executionId, log.TaskId);
        Assert.Equal(StartedAt, log.StartTime);
        Assert.Equal(FinishedAt, log.EndTime);
        Assert.False(log.Success);
        Assert.Equal(1, log.Attempt);
        Assert.Equal("boom", log.ErrorMessage);
    }

    [Fact]
    public async Task RecordAsync_MissingOwner_PausesJobAndClearsActiveAttempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var executionId = Guid.CreateVersion7();
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            AgentType = AgentRuntimeType.Agent,
            AgentId = Guid.CreateVersion7(),
            Name = "invalid-owner-job",
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:15:00",
            NextRunTime = StartedAt,
            Status = JobStatus.Running,
            IsEnabled = true,
            ActiveExecutionId = executionId,
            ActiveAttemptStartedAt = StartedAt,
            CreateBy = string.Empty,
            CreateTime = StartedAt,
        };
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);

        var recorder = new JobAttemptOutcomeRecorder(
            dbContext,
            new JobOutcomeTransaction(dbContext, InMemoryApplicationLock.Shared),
            null!,
            new TestTimeProvider(FinishedAt)
        );

        var result = await recorder.RecordAsync(job.Id, executionId, false, "owner missing", cancellationToken);

        Assert.IsType<JobAttemptResult.Drop>(result);
        dbContext.ChangeTracker.Clear();
        using var systemScope = UserInfoUtil.PushSystemScope();
        var persistedJob = await dbContext.Jobs.SingleAsync(cancellationToken);
        var log = await dbContext.JobLogs.SingleAsync(cancellationToken);
        Assert.Equal(JobStatus.Paused, persistedJob.Status);
        Assert.False(persistedJob.IsEnabled);
        Assert.Null(persistedJob.ActiveExecutionId);
        Assert.Null(persistedJob.ActiveAttemptStartedAt);
        Assert.Equal("The Job owner is missing.", persistedJob.LastError);
        Assert.Equal("scheduler", log.CreateBy);
        Assert.False(log.Success);
    }
}
