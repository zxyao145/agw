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
    public async Task ListResponsesAsync_GroupsTasksByContextId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();
        var otherContextTaskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectTasks.AddRange(
                CreateTask(firstTaskId, projectId, "context-1", "Plan trip", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(secondTaskId, projectId, "context-1", "Find hotels", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(otherContextTaskId, projectId, "context-2", "Other", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)));
            seedContext.TaskRecords.AddRange(
                CreateRecord(firstTaskId, 0, "Tokyo trip"),
                CreateRecord(secondTaskId, 0, "Hotels"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var contexts = await service.ListResponsesAsync(projectId);

        Assert.Equal(2, contexts.Count);
        var context = Assert.Single(contexts, item => item.ContextId == "context-1");
        Assert.Equal(projectId.Normalize(), context.ProjectId);
        Assert.Equal(2, context.TaskCount);
        Assert.Equal(2, context.MessageCount);
        Assert.Equal(secondTaskId, context.LatestTaskId);
        Assert.Equal("Find hotels", context.Title);
    }

    [Fact]
    public async Task GetResponseAsync_ReturnsMessagesForOnlyRequestedProjectContext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();
        var otherContextTaskId = Guid.NewGuid();
        var otherProjectTaskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.AddRange(
                CreateProject(projectId, "Project"),
                CreateProject(otherProjectId, "Other Project"));
            seedContext.ProjectTasks.AddRange(
                CreateTask(firstTaskId, projectId, "context-1", "Plan trip", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(secondTaskId, projectId, "context-1", "Find hotels", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(otherContextTaskId, projectId, "context-2", "Other context", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(otherProjectTaskId, otherProjectId, "context-1", "Other project", new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)));
            seedContext.TaskRecords.AddRange(
                CreateRecord(secondTaskId, 0, "Hotels"),
                CreateRecord(firstTaskId, 0, "Tokyo trip"),
                CreateRecord(otherContextTaskId, 0, "Wrong context"),
                CreateRecord(otherProjectTaskId, 0, "Wrong project"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var context = await service.GetResponseAsync(projectId, "context-1");

        Assert.NotNull(context);
        Assert.Equal(projectId.Normalize(), context.ProjectId);
        Assert.Equal("context-1", context.ContextId);
        Assert.Equal(secondTaskId, context.LatestTaskId);
        Assert.Equal([firstTaskId, secondTaskId], context.Tasks.Select(task => task.Id));
        Assert.Equal(2, context.MessageCount);
        Assert.Equal(["Tokyo trip", "Hotels"], context.Messages!.Select(GetMessageText));
    }

    [Fact]
    public async Task GetResponseByTaskIdAsync_ReturnsContainingProjectContext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectTasks.AddRange(
                CreateTask(firstTaskId, projectId, "context-1", "Plan trip", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(secondTaskId, projectId, "context-1", "Find hotels", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
            seedContext.TaskRecords.AddRange(
                CreateRecord(firstTaskId, 0, "Tokyo trip"),
                CreateRecord(secondTaskId, 0, "Hotels"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var context = await service.GetResponseByTaskIdAsync(projectId, firstTaskId);

        Assert.NotNull(context);
        Assert.Equal("context-1", context.ContextId);
        Assert.Equal(secondTaskId, context.LatestTaskId);
        Assert.Equal(["Tokyo trip", "Hotels"], context.Messages!.Select(GetMessageText));
    }

    [Fact]
    public async Task ClearRecordsAsync_RemovesOnlyRequestedProjectContextRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();
        var otherContextTaskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectTasks.AddRange(
                CreateTask(firstTaskId, projectId, "context-1", "Plan trip", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(secondTaskId, projectId, "context-1", "Find hotels", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(otherContextTaskId, projectId, "context-2", "Other context", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)));
            seedContext.TaskRecords.AddRange(
                CreateRecord(firstTaskId, 0, "Tokyo trip"),
                CreateRecord(secondTaskId, 0, "Hotels"),
                CreateRecord(otherContextTaskId, 0, "Other"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var dbContext = new AgwDbContext(options))
        {
            var service = CreateService(dbContext);

            var result = await service.ClearRecordsAsync(projectId, "context-1");

            Assert.Equal(ApplicationResultType.Success, result.Type);
        }

        await using var assertContext = new AgwDbContext(options);
        var remainingRecord = Assert.Single(assertContext.TaskRecords);
        Assert.Equal(otherContextTaskId, remainingRecord.TaskId);
        Assert.Equal(3, assertContext.ProjectTasks.Count());
    }

    [Fact]
    public async Task UpdateTitleAsync_UpdatesOnlyRequestedProjectContextTasks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();
        var otherContextTaskId = Guid.NewGuid();
        var otherProjectTaskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.AddRange(
                CreateProject(projectId, "Project"),
                CreateProject(otherProjectId, "Other Project"));
            seedContext.ProjectTasks.AddRange(
                CreateTask(firstTaskId, projectId, "context-1", "Plan trip", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(secondTaskId, projectId, "context-1", "Find hotels", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(otherContextTaskId, projectId, "context-2", "Other context", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(otherProjectTaskId, otherProjectId, "context-1", "Other project", new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var dbContext = new AgwDbContext(options))
        {
            var service = CreateService(dbContext);

            var result = await service.UpdateTitleAsync(projectId, "context-1", "  Tokyo planning  ", "renamer");

            Assert.Equal(ApplicationResultType.Success, result.Type);
        }

        await using var assertContext = new AgwDbContext(options);
        Assert.Equal(
            ["Tokyo planning"],
            assertContext.ProjectTasks
                .Where(task => task.ProjectId == projectId && task.ContextId == "context-1")
                .Select(task => task.Title)
                .Distinct()
                .ToList());
        Assert.Equal("Other context", assertContext.ProjectTasks.Single(task => task.Id == otherContextTaskId).Title);
        Assert.Equal("Other project", assertContext.ProjectTasks.Single(task => task.Id == otherProjectTaskId).Title);
    }

    [Fact]
    public async Task UpdateTitleAsync_WhenTitleBlank_ReturnsInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var result = await service.UpdateTitleAsync(Guid.NewGuid(), "context-1", "   ", "renamer");

        Assert.Equal(ApplicationResultType.Invalid, result.Type);
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyRequestedProjectContextTasksAndRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();
        var otherContextTaskId = Guid.NewGuid();
        var otherProjectTaskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.AddRange(
                CreateProject(projectId, "Project"),
                CreateProject(otherProjectId, "Other Project"));
            seedContext.ProjectTasks.AddRange(
                CreateTask(firstTaskId, projectId, "context-1", "Plan trip", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(secondTaskId, projectId, "context-1", "Find hotels", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(otherContextTaskId, projectId, "context-2", "Other context", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(otherProjectTaskId, otherProjectId, "context-1", "Other project", new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)));
            seedContext.TaskRecords.AddRange(
                CreateRecord(firstTaskId, 0, "Tokyo trip"),
                CreateRecord(secondTaskId, 0, "Hotels"),
                CreateRecord(otherContextTaskId, 0, "Wrong context"),
                CreateRecord(otherProjectTaskId, 0, "Wrong project"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var dbContext = new AgwDbContext(options))
        {
            var service = CreateService(dbContext);

            var deleted = await service.DeleteAsync(projectId, "context-1");

            Assert.True(deleted);
        }

        await using var assertContext = new AgwDbContext(options);
        Assert.Equal(
            [otherContextTaskId, otherProjectTaskId],
            assertContext.ProjectTasks.Select(task => task.Id).ToHashSet());
        Assert.Equal(
            [otherContextTaskId, otherProjectTaskId],
            assertContext.TaskRecords.Select(record => record.TaskId).ToHashSet());
    }

    [Fact]
    public async Task DeleteAllAsync_RemovesOnlyRequestedProjectTasksAndRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();
        var otherProjectTaskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.AddRange(
                CreateProject(projectId, "Project"),
                CreateProject(otherProjectId, "Other Project"));
            seedContext.ProjectTasks.AddRange(
                CreateTask(firstTaskId, projectId, "context-1", "Plan trip", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(secondTaskId, projectId, "context-2", "Find hotels", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
                CreateTask(otherProjectTaskId, otherProjectId, "context-1", "Other project", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)));
            seedContext.TaskRecords.AddRange(
                CreateRecord(firstTaskId, 0, "Tokyo trip"),
                CreateRecord(secondTaskId, 0, "Hotels"),
                CreateRecord(otherProjectTaskId, 0, "Wrong project"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var dbContext = new AgwDbContext(options))
        {
            var service = CreateService(dbContext);

            var result = await service.DeleteAllAsync(projectId);

            Assert.Equal(ApplicationResultType.Success, result.Type);
        }

        await using var assertContext = new AgwDbContext(options);
        var task = Assert.Single(assertContext.ProjectTasks);
        Assert.Equal(otherProjectTaskId, task.Id);
        var record = Assert.Single(assertContext.TaskRecords);
        Assert.Equal(otherProjectTaskId, record.TaskId);
    }

    [Fact]
    public async Task GetResponseAsync_WhenContextMissing_ReturnsNull()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var context = await service.GetResponseAsync(projectId, "missing-context");

        Assert.Null(context);
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

    private static ProjectTask CreateTask(Guid taskId, Guid projectId, string contextId, string title, DateTime createTime) => new()
    {
        Id = taskId,
        ProjectId = projectId,
        ContextId = contextId,
        Title = title,
        Status = ProjectTaskStatus.Succeeded,
        CreateBy = "tester",
        CreateTime = createTime,
        UpdateBy = "tester",
        UpdateTime = createTime,
        FinishedTime = createTime
    };

    private static string? GetMessageText(AgwMessage message) =>
        (message.Contents[0] as AgwTextContent)?.Content;

    private static TaskRecord CreateRecord(Guid taskId, long sequence, string text) => new()
    {
        Id = Guid.NewGuid(),
        TaskId = taskId,
        ConversationSequence = sequence,
        ConversationPayload = JsonUtil.Serialize(new ChatMessage(ChatRole.User, text)
        {
            MessageId = Guid.NewGuid().ToString(),
            AuthorName = Constants.DefaultAuthor
        }),
        CreateTime = DateTime.UtcNow
    };

    private static ProjectContextAppService CreateService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);

        return new ProjectContextAppService(
            new EfRepository<ProjectTask>(dbContext),
            new EfRepository<TaskRecord>(dbContext),
            new UnitOfWork(dbContext),
            new ProjectResolver(projectRepository),
            new ProjectTaskDomainService(),
            new TaskRecordDomainService());
    }
}
