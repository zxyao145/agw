using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Projects.Application;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Projects;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class TaskAppServiceTests
{
    [Fact]
    public void ITaskAppService_ExposesOnlyReducedCreateTaskForExecutionSignature()
    {
        var methods = typeof(ITaskAppService)
            .GetMethods()
            .Where(method => method.Name == nameof(ITaskAppService.CreateTaskForExecutionAsync))
            .ToArray();

        var method = Assert.Single(methods);
        var parameters = method.GetParameters();

        Assert.Collection(
            parameters,
            parameter => Assert.Equal("projectId", parameter.Name),
            parameter => Assert.Equal("taskId", parameter.Name),
            parameter => Assert.Equal("input", parameter.Name),
            parameter => Assert.Equal("user", parameter.Name),
            parameter => Assert.Equal("contextId", parameter.Name),
            parameter => Assert.Equal("cancellationToken", parameter.Name));
    }

    [Fact]
    public async Task CreateTaskForExecutionAsync_CreatesChatTaskWithoutTargetBinding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var projectId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Chat Project",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var task = await service.CreateTaskForExecutionAsync(
            projectId,
            taskId: null,
            input: "  hello world  ",
            user: "tester",
            cancellationToken: cancellationToken);

        Assert.NotNull(task);
        Assert.Null(task!.JobId);
        Assert.Equal("hello world", task.Title);
        Assert.NotNull(await dbContext.ProjectContexts.SingleOrDefaultAsync(cancellationToken));
        Assert.NotNull(await dbContext.TaskRecords.SingleOrDefaultAsync(
            record => record.TaskId == task.TaskId,
            cancellationToken));
    }

    [Fact]
    public async Task ResolveTaskAsync_WhenResumeUsesContextId_ReturnsLatestTaskInProjectContext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var projectId = Guid.NewGuid();
        var contextRowId = Guid.NewGuid();
        var oldTaskId = Guid.NewGuid();
        var latestTaskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Chat Project",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            seedContext.ProjectContexts.Add(new ProjectContext
            {
                Id = contextRowId,
                ProjectId = projectId,
                ContextId = "context-1",
                Title = "Chat",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow().AddMinutes(-2),
                UpdateBy = "tester",
                UpdateTime = TimeProvider.System.GetUtcNow().AddMinutes(-1)
            });
            seedContext.TaskRecords.AddRange(
                new TaskRecord
                {
                    Id = Guid.NewGuid(),
                    ProjectContextId = contextRowId,
                    TaskId = oldTaskId,
                    Status = TaskExecutionStatus.Succeeded,
                    CreateTime = TimeProvider.System.GetUtcNow().AddMinutes(-2),
                    UpdateTime = TimeProvider.System.GetUtcNow().AddMinutes(-2)
                },
                new TaskRecord
                {
                    Id = Guid.NewGuid(),
                    ProjectContextId = contextRowId,
                    TaskId = latestTaskId,
                    Status = TaskExecutionStatus.Running,
                    CreateTime = TimeProvider.System.GetUtcNow().AddMinutes(-1),
                    UpdateTime = TimeProvider.System.GetUtcNow()
                });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var result = await service.ResolveTaskAsync(
            new ExecutionTaskRequest(
                TaskId: null,
                ProjectId: projectId,
                ContextId: "context-1",
                Input: "resume",
                Resume: true,
                User: "tester"),
            cancellationToken);

        Assert.Null(result.Error);
        Assert.NotNull(result.Task);
        Assert.Equal(latestTaskId, result.Task!.TaskId);
        Assert.Equal("context-1", result.Task.ContextId);
    }

    private static TaskAppService CreateService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);
        var taskExecutionAppService = new TaskExecutionAppService(
            new EfRepository<ProjectContext>(dbContext),
            new EfRepository<TaskRecord>(dbContext),
            new UnitOfWork(dbContext),
            new Domain.Services.TaskRecordDomainService(),
            new ProjectResolver(projectRepository),
            TimeProvider.System);

        return new TaskAppService(
            new EfRepository<ProjectContext>(dbContext),
            new EfRepository<TaskRecord>(dbContext),
            new ProjectResolver(projectRepository),
            taskExecutionAppService);
    }
}
