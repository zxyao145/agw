using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Enums;
using Agw.Shared.Tasks.Entities;
using Agw.Tasks.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class TaskAppServiceTests
{
    [Fact]
    public async Task CreateTaskForExecutionAsync_CreatesChatTaskWithoutTargetBinding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new LlmDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var projectId = Guid.NewGuid();
        await using (var seedContext = new LlmDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Chat Project",
                Type = ProjectType.UserDefined,
                Enable = true,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new LlmDbContext(options);
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
    }

    private static TaskAppService CreateService(LlmDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);
        var projectTaskAppService = new ProjectTaskAppService(
            new EfRepository<ProjectTask>(dbContext),
            new EfRepository<TaskRecord>(dbContext),
            new UnitOfWork(dbContext),
            new Domain.Services.ProjectTaskDomainService(),
            new Domain.Services.TaskRecordDomainService(),
            new ProjectResolver(projectRepository));

        return new TaskAppService(
            new EfRepository<ProjectTask>(dbContext),
            new EfRepository<TaskRecord>(dbContext),
            new ProjectResolver(projectRepository),
            projectTaskAppService);
    }
}
