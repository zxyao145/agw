using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Enums;
using Agw.Tasks.Application;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

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
                Enable = true,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
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
    }

    private static TaskAppService CreateService(AgwDbContext dbContext)
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
