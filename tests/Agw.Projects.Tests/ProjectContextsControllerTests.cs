using System.Reflection;

using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Projects.Application;
using Agw.Projects.Controllers;
using Agw.Projects.Domain.Services;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class ProjectContextsControllerTests
{
    [Fact]
    public void ProjectContextsController_UsesProjectContextsRoute()
    {
        var attribute = Assert.Single(typeof(ProjectContextsController).GetCustomAttributes<RouteAttribute>());

        Assert.Equal("api/projects/{projectId}/contexts", attribute.Template);
    }

    [Fact]
    public void GetAsync_UsesContextIdRoute()
    {
        var method = GetGetMethod();
        var attribute = Assert.Single(method.GetCustomAttributes<HttpGetAttribute>());

        Assert.Equal("{contextId}", attribute.Template);
    }

    [Fact]
    public void ProjectContextsController_DoesNotExposeTaskIdRoute()
    {
        var taskIdRoutes = typeof(ProjectContextsController)
            .GetMethods()
            .SelectMany(method => method.GetCustomAttributes<HttpGetAttribute>())
            .Select(attribute => attribute.Template)
            .Where(template => template?.Contains("task", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.Empty(taskIdRoutes);
    }

    [Fact]
    public void ClearRecordsAsync_UsesContextClearRecordsRoute()
    {
        var method = GetClearRecordsMethod();
        var attribute = Assert.Single(method.GetCustomAttributes<HttpDeleteAttribute>());

        Assert.Equal("{contextId}/clear-records", attribute.Template);
    }

    [Fact]
    public void UpdateTitleAsync_UsesContextTitleRoute()
    {
        var method = GetUpdateTitleMethod();
        var attribute = Assert.Single(method.GetCustomAttributes<HttpPutAttribute>());

        Assert.Equal("{contextId}/title", attribute.Template);
    }

    [Fact]
    public void DeleteAllAsync_UsesProjectContextsRoute()
    {
        var method = GetDeleteAllMethod();
        var attribute = Assert.Single(method.GetCustomAttributes<HttpDeleteAttribute>());

        Assert.Null(attribute.Template);
    }

    [Fact]
    public void DeleteAsync_UsesContextIdRoute()
    {
        var method = GetDeleteMethod();
        var attribute = Assert.Single(method.GetCustomAttributes<HttpDeleteAttribute>());

        Assert.Equal("{contextId}", attribute.Template);
    }

    [Fact]
    public async Task ListAsync_ReturnsApiResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Project",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectContextsController(CreateService(dbContext));

        var result = await controller.ListAsync(projectId);

        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
    }

    [Fact]
    public async Task ClearRecordsAsync_WhenContextMissing_ReturnsApiResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Project",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectContextsController(CreateService(dbContext));

        var result = await controller.ClearRecordsAsync(projectId, "missing-context");

        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
    }

    [Fact]
    public async Task DeleteAsync_WhenContextMissing_ReturnsApiResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Project",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectContextsController(CreateService(dbContext));

        var result = await controller.DeleteAsync(projectId, "missing-context");

        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
    }

    [Fact]
    public async Task UpdateTitleAsync_WhenTitleBlank_ReturnsApiResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Project",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectContextsController(CreateService(dbContext));

        var result = await controller.UpdateTitleAsync(
            projectId,
            "context-1",
            new ProjectContextTitleUpdateRequest("   "));

        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
    }

    [Fact]
    public async Task GetAsync_WhenContextMissing_ReturnsApiResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Project",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectContextsController(CreateService(dbContext));

        var result = await controller.GetAsync(projectId, "missing-context");

        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
    }

    private static MethodInfo GetGetMethod()
    {
        var method = typeof(ProjectContextsController).GetMethod(
            "GetAsync",
            [
                typeof(Guid),
                typeof(string)
            ]);

        Assert.NotNull(method);
        return method;
    }

    private static MethodInfo GetClearRecordsMethod()
    {
        var method = typeof(ProjectContextsController).GetMethod(
            "ClearRecordsAsync",
            [
                typeof(Guid),
                typeof(string)
            ]);

        Assert.NotNull(method);
        return method;
    }

    private static MethodInfo GetUpdateTitleMethod()
    {
        var method = typeof(ProjectContextsController).GetMethod(
            "UpdateTitleAsync",
            [
                typeof(Guid),
                typeof(string),
                typeof(ProjectContextTitleUpdateRequest)
            ]);

        Assert.NotNull(method);
        return method;
    }

    private static MethodInfo GetDeleteAllMethod()
    {
        var method = typeof(ProjectContextsController).GetMethod(
            "DeleteAllAsync",
            [
                typeof(Guid)
            ]);

        Assert.NotNull(method);
        return method;
    }

    private static MethodInfo GetDeleteMethod()
    {
        var method = typeof(ProjectContextsController).GetMethod(
            "DeleteAsync",
            [
                typeof(Guid),
                typeof(string)
            ]);

        Assert.NotNull(method);
        return method;
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

    private static ProjectContextAppService CreateService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);

        return new ProjectContextAppService(
            new EfRepository<ProjectConversation>(dbContext),
            new EfRepository<ProjectConversationChatHistory>(dbContext),
            new EfRepository<AgentflowCheckpointRecord>(dbContext),
            new EfRepository<AgentflowTrace>(dbContext),
            new EfRepository<AgentUsage>(dbContext),
            dbContext,
            new ProjectResolver(projectRepository),
            new ProjectConversationChatHistoryDomainService(),
            new TaskSessionBindingService(
                new EfRepository<TaskSessionBinding>(dbContext),
                new EfRepository<ProjectConversation>(dbContext),
                dbContext,
                TimeProvider.System),
            TimeProvider.System);
    }
}
