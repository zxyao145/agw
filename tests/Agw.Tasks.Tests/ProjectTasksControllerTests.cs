using System.Reflection;

using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Tasks.Application;
using Agw.Tasks.Controllers;
using Agw.Tasks.Domain.Services;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class ProjectTasksControllerTests
{
    [Fact]
    public async Task UpdateTitleAsync_WhenTaskBelongsToProject_UpdatesTitle()
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
        var taskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Project",
                Type = ProjectType.UserDefined,
                Enable = true,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });

            seedContext.ProjectTasks.Add(new ProjectTask
            {
                Id = taskId,
                ProjectId = projectId,
                ContextId = "context-1",
                Title = "Original",
                Status = ProjectTaskStatus.Running,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectTasksController(CreateService(dbContext));
        var method = GetUpdateTitleMethod();

        var result = await InvokeUpdateTitleAsync(
            method,
            controller,
            projectId,
            taskId,
            new ProjectTaskTitleUpdateRequest("  Renamed task  "));

        Assert.Equal("Bens.Results.ApiResult", result.GetType().FullName);
        var task = await dbContext.ProjectTasks.SingleAsync(x => x.Id == taskId, cancellationToken);
        Assert.Equal("Renamed task", task.Title);
        Assert.Equal("system", task.UpdateBy);
    }

    [Fact]
    public async Task UpdateTitleAsync_WhenTitleIsBlank_ReturnsBadRequest()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Project",
                Type = ProjectType.UserDefined,
                Enable = true,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });

            seedContext.ProjectTasks.Add(new ProjectTask
            {
                Id = taskId,
                ProjectId = projectId,
                ContextId = "context-1",
                Title = "Original",
                Status = ProjectTaskStatus.Running,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });

            await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectTasksController(CreateService(dbContext));
        var method = GetUpdateTitleMethod();

        var result = await InvokeUpdateTitleAsync(
            method,
            controller,
            projectId,
            taskId,
            new ProjectTaskTitleUpdateRequest("   "));

        Assert.Equal("Bens.Results.ApiResult", result.GetType().FullName);
        var task = await dbContext.ProjectTasks.SingleAsync(x => x.Id == taskId, TestContext.Current.CancellationToken);
        Assert.Equal("Original", task.Title);
    }

    private static MethodInfo GetUpdateTitleMethod()
    {
        var method = typeof(ProjectTasksController).GetMethod(
            "UpdateTitleAsync",
            [
                typeof(Guid),
                typeof(Guid),
                typeof(ProjectTaskTitleUpdateRequest)
            ]);

        Assert.NotNull(method);

        var attribute = Assert.Single(method.GetCustomAttributes<HttpPutAttribute>());
        Assert.Equal("{taskId:guid}/title", attribute.Template);
        return method;
    }

    private static async Task<IActionResult> InvokeUpdateTitleAsync(
        MethodInfo method,
        ProjectTasksController controller,
        Guid projectId,
        Guid taskId,
        ProjectTaskTitleUpdateRequest request)
    {
        var result = method.Invoke(controller, [projectId, taskId, request]);
        var task = Assert.IsAssignableFrom<Task<IActionResult>>(result);
        return await task;
    }

    private static ProjectTaskAppService CreateService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);

        return new ProjectTaskAppService(
            new EfRepository<ProjectTask>(dbContext),
            new EfRepository<TaskRecord>(dbContext),
            new UnitOfWork(dbContext),
            new ProjectTaskDomainService(),
            new TaskRecordDomainService(),
            new ProjectResolver(projectRepository));
    }
}
