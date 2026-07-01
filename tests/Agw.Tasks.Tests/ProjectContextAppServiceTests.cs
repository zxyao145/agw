using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;
using Agw.Tasks.Application;
using Agw.Tasks.Domain.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Tasks.Tests;

public class ProjectContextAppServiceTests
{
    [Fact]
    public async Task ListResponsesAsync_GroupsTasksFromRecordsByContext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var contextId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectContexts.Add(CreateContext(contextId, projectId, "context-1", "Trip", jobId));
            seedContext.TaskRecords.AddRange(
                CreateRecord(contextId, firstTaskId, 0, "Tokyo trip", TaskExecutionStatus.Succeeded),
                CreateRecord(contextId, secondTaskId, 0, "Hotels", TaskExecutionStatus.Running));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var contexts = await service.ListResponsesAsync(projectId);

        var context = Assert.Single(contexts);
        Assert.Equal(projectId.Normalize(), context.ProjectId);
        Assert.Equal("context-1", context.ContextId);
        Assert.Equal(jobId, context.JobId);
        Assert.Equal("Trip", context.Title);
        Assert.Equal(2, context.TaskCount);
        Assert.Equal(2, context.MessageCount);
        Assert.Equal(secondTaskId, context.LatestTaskId);
        Assert.Equal(TaskExecutionStatus.Running, context.LatestStatus);
    }

    [Fact]
    public async Task ListResponsesAsync_SkipsContextsWithoutMessages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var transientContextId = Guid.NewGuid();
        var emptyTitledContextId = Guid.NewGuid();
        var persistedContextId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectContexts.AddRange(
                CreateContext(transientContextId, projectId, "pending-context", "New Chat"),
                CreateContext(emptyTitledContextId, projectId, "empty-titled-context", "Queued run"),
                CreateContext(persistedContextId, projectId, "persisted-context", "Persisted"));
            seedContext.TaskRecords.AddRange(
                new TaskRecord
                {
                    Id = Guid.NewGuid(),
                    ProjectContextId = transientContextId,
                    TaskId = Guid.NewGuid(),
                    Status = TaskExecutionStatus.Running,
                    CreateTime = DateTime.UtcNow,
                    UpdateTime = DateTime.UtcNow
                },
                new TaskRecord
                {
                    Id = Guid.NewGuid(),
                    ProjectContextId = emptyTitledContextId,
                    TaskId = Guid.NewGuid(),
                    Status = TaskExecutionStatus.Running,
                    CreateTime = DateTime.UtcNow,
                    UpdateTime = DateTime.UtcNow
                },
                CreateRecord(
                    persistedContextId,
                    Guid.NewGuid(),
                    0,
                    "hello",
                    TaskExecutionStatus.Running));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var contexts = await service.ListResponsesAsync(projectId);

        var context = Assert.Single(contexts);
        Assert.Equal("persisted-context", context.ContextId);
    }

    [Fact]
    public async Task GetResponseAsync_ReturnsMessagesForRequestedContextOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var contextId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var otherContextId = Guid.NewGuid();
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();
        var otherTaskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectContexts.AddRange(
                CreateContext(contextId, projectId, "context-1", "Trip", jobId),
                CreateContext(otherContextId, projectId, "context-2", "Other"));
            seedContext.TaskRecords.AddRange(
                CreateRecord(contextId, firstTaskId, 0, "Tokyo trip", TaskExecutionStatus.Succeeded),
                CreateRecord(contextId, secondTaskId, 0, "Hotels", TaskExecutionStatus.Succeeded),
                CreateRecord(otherContextId, otherTaskId, 0, "Wrong context", TaskExecutionStatus.Succeeded));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var context = await service.GetResponseAsync(projectId, "context-1");

        Assert.NotNull(context);
        Assert.Equal(jobId, context.JobId);
        Assert.Equal([firstTaskId, secondTaskId], context.Tasks.Select(task => task.TaskId));
        Assert.Equal(["Tokyo trip", "Hotels"], context.Messages!.Select(GetMessageText));
    }

    [Fact]
    public async Task DeleteAsync_RemovesContextAndRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var contextId = Guid.NewGuid();
        var otherContextId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectContexts.AddRange(
                CreateContext(contextId, projectId, "context-1", "Trip"),
                CreateContext(otherContextId, projectId, "context-2", "Other"));
            seedContext.TaskRecords.AddRange(
                CreateRecord(contextId, Guid.NewGuid(), 0, "Delete me", TaskExecutionStatus.Succeeded),
                CreateRecord(otherContextId, Guid.NewGuid(), 0, "Keep me", TaskExecutionStatus.Succeeded));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var dbContext = new AgwDbContext(options))
        {
            var service = CreateService(dbContext);
            var deleted = await service.DeleteAsync(projectId, "context-1");
            Assert.True(deleted);
        }

        await using var assertContext = new AgwDbContext(options);
        var remainingContext = Assert.Single(assertContext.ProjectContexts);
        Assert.Equal("context-2", remainingContext.ContextId);
        var remainingRecord = Assert.Single(assertContext.TaskRecords);
        Assert.Equal(otherContextId, remainingRecord.ProjectContextId);
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

    private static async Task EnsureCreatedAsync(DbContextOptions<AgwDbContext> options, CancellationToken cancellationToken)
    {
        await using var setupContext = new AgwDbContext(options);
        await setupContext.Database.EnsureCreatedAsync(cancellationToken);
    }

    private static Project CreateProject(Guid projectId, string name) => new()
    {
        Id = projectId,
        Name = name,
        Type = ProjectType.UserDefined,
        Enable = true,
        CreateBy = "tester",
        CreateTime = DateTime.UtcNow
    };

    private static ProjectContext CreateContext(
        Guid id,
        Guid projectId,
        string contextId,
        string title,
        Guid? jobId = null) => new()
    {
        Id = id,
        ProjectId = projectId,
        ContextId = contextId,
        JobId = jobId,
        Title = title,
        CreateBy = "tester",
        CreateTime = DateTime.UtcNow,
        UpdateBy = "tester",
        UpdateTime = DateTime.UtcNow
    };

    private static TaskRecord CreateRecord(
        Guid contextId,
        Guid taskId,
        long sequence,
        string text,
        TaskExecutionStatus status) => new()
    {
        Id = Guid.NewGuid(),
        ProjectContextId = contextId,
        TaskId = taskId,
        Status = status,
        ConversationSequence = sequence,
        ConversationPayload = JsonUtil.Serialize(new ChatMessage(ChatRole.User, text)
        {
            MessageId = Guid.NewGuid().ToString(),
            AuthorName = Constants.DefaultInputAuthor
        }),
        CreateTime = DateTime.UtcNow,
        UpdateTime = DateTime.UtcNow
    };

    private static string? GetMessageText(AgwMessage message) =>
        (message.Contents[0] as AgwTextContent)?.Content;

    private static ProjectContextAppService CreateService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);

        return new ProjectContextAppService(
            new EfRepository<ProjectContext>(dbContext),
            new EfRepository<TaskRecord>(dbContext),
            new UnitOfWork(dbContext),
            new ProjectResolver(projectRepository),
            new TaskRecordDomainService());
    }
}
