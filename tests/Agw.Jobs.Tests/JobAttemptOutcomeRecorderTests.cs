using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Projects.Application;
using Agw.Projects.Application.Facades;
using Agw.Projects.Domain.Services;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Tests;

public sealed class JobAttemptOutcomeRecorderTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FinishedAt = StartedAt.AddMinutes(2);

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

        var jobRepository = new JobRepo(dbContext, new TestTimeProvider(FinishedAt));
        var jobLogRepository = new EfRepository<JobLog>(dbContext);
        var contextRepository = new EfRepository<ProjectConversation>(dbContext);
        var historyRepository = new EfRepository<ProjectConversationChatHistory>(dbContext);
        var taskExecution = new TaskExecutionAppService(
            contextRepository,
            historyRepository,
            dbContext,
            new ProjectConversationChatHistoryDomainService(),
            new ProjectResolver(new EfRepository<Project>(dbContext)),
            new TestTimeProvider(FinishedAt)
        );
        var taskResolver = new TaskAppService(
            contextRepository,
            historyRepository,
            new ProjectResolver(new EfRepository<Project>(dbContext)),
            taskExecution
        );
        var recorder = new JobAttemptOutcomeRecorder(
            jobRepository,
            jobLogRepository,
            dbContext,
            new ProjectTaskFacade(taskExecution, contextRepository, historyRepository, taskResolver),
            new JobScheduleCalculator(),
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
}
