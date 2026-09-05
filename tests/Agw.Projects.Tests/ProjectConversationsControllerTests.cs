using System.Reflection;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Projects.Controllers;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class ProjectConversationsControllerTests
{
    [Fact]
    public void ProjectConversationsController_UsesProjectConversationsRoute()
    {
        var attribute = Assert.Single(typeof(ProjectConversationsController).GetCustomAttributes<RouteAttribute>());

        Assert.Equal("api/projects/{projectId}/conversations", attribute.Template);
    }

    [Fact]
    public void ProjectConversationsController_DoesNotExposePostRoute()
    {
        var postRoutes = typeof(ProjectConversationsController)
            .GetMethods()
            .SelectMany(method => method.GetCustomAttributes<HttpPostAttribute>());

        Assert.Empty(postRoutes);
    }

    [Fact]
    public void GetAsync_UsesContextIdRoute()
    {
        var method = GetGetMethod();
        var attribute = Assert.Single(method.GetCustomAttributes<HttpGetAttribute>());

        Assert.Equal("{conversationId}", attribute.Template);
    }

    [Fact]
    public void GetMessagesAsync_UsesConversationMessagesRoute()
    {
        var method = typeof(ProjectConversationsController).GetMethod(
            "GetMessagesAsync",
            [typeof(Guid), typeof(Guid), typeof(ProjectConversationMessagesQuery), typeof(CancellationToken)]
        );
        Assert.NotNull(method);
        var attribute = Assert.Single(method.GetCustomAttributes<HttpGetAttribute>());

        Assert.Equal("{conversationId}/messages", attribute.Template);
    }

    [Fact]
    public void ProjectConversationsController_DoesNotExposeTaskIdRoute()
    {
        var taskIdRoutes = typeof(ProjectConversationsController)
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

        Assert.Equal("{conversationId}/clear-records", attribute.Template);
    }

    [Fact]
    public void UpdateTitleAsync_UsesContextTitleRoute()
    {
        var method = GetUpdateTitleMethod();
        var attribute = Assert.Single(method.GetCustomAttributes<HttpPutAttribute>());

        Assert.Equal("{conversationId}/title", attribute.Template);
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

        Assert.Equal("{conversationId}", attribute.Template);
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
            seedContext.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "Project",
                    Type = ProjectType.UserDefined,
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectConversationsController(CreateService(dbContext));

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
            seedContext.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "Project",
                    Type = ProjectType.UserDefined,
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectConversationsController(CreateService(dbContext));

        var result = await controller.ClearRecordsAsync(projectId, Guid.CreateVersion7());

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
            seedContext.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "Project",
                    Type = ProjectType.UserDefined,
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectConversationsController(CreateService(dbContext));

        var result = await controller.DeleteAsync(projectId, Guid.CreateVersion7());

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
            seedContext.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "Project",
                    Type = ProjectType.UserDefined,
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectConversationsController(CreateService(dbContext));

        var result = await controller.UpdateTitleAsync(
            projectId,
            Guid.CreateVersion7(),
            new ProjectConversationTitleUpdateRequest("   ")
        );

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
            seedContext.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "Project",
                    Type = ProjectType.UserDefined,
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var controller = new ProjectConversationsController(CreateService(dbContext));

        var result = await controller.GetAsync(projectId, Guid.CreateVersion7(), cancellationToken);

        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
    }

    private static MethodInfo GetGetMethod()
    {
        var method = typeof(ProjectConversationsController).GetMethod(
            "GetAsync",
            [typeof(Guid), typeof(Guid), typeof(CancellationToken)]
        );

        Assert.NotNull(method);
        return method;
    }

    private static MethodInfo GetClearRecordsMethod()
    {
        var method = typeof(ProjectConversationsController).GetMethod(
            "ClearRecordsAsync",
            [typeof(Guid), typeof(Guid)]
        );

        Assert.NotNull(method);
        return method;
    }

    private static MethodInfo GetUpdateTitleMethod()
    {
        var method = typeof(ProjectConversationsController).GetMethod(
            "UpdateTitleAsync",
            [typeof(Guid), typeof(Guid), typeof(ProjectConversationTitleUpdateRequest)]
        );

        Assert.NotNull(method);
        return method;
    }

    private static MethodInfo GetDeleteAllMethod()
    {
        var method = typeof(ProjectConversationsController).GetMethod("DeleteAllAsync", [typeof(Guid)]);

        Assert.NotNull(method);
        return method;
    }

    private static MethodInfo GetDeleteMethod()
    {
        var method = typeof(ProjectConversationsController).GetMethod("DeleteAsync", [typeof(Guid), typeof(Guid)]);

        Assert.NotNull(method);
        return method;
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).UseSnakeCaseNamingConvention().Options;

    private static async Task EnsureCreatedAsync(
        DbContextOptions<AgwDbContext> options,
        CancellationToken cancellationToken
    )
    {
        await using var setupContext = new AgwDbContext(options);
        await setupContext.Database.EnsureCreatedAsync(cancellationToken);
    }

    private static ProjectConversationAppService CreateService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);
        var userInfo = new TestUserInfoService();
        var projectResolver = new ProjectResolver(projectRepository, userInfo);

        return new ProjectConversationAppService(
            new EfRepository<ProjectConversation>(dbContext),
            new EfRepository<ProjectConversationChatHistory>(dbContext),
            new EfRepository<AgentUsage>(dbContext),
            dbContext,
            projectResolver,
            TestProjectPersistence.CreateDeletionCoordinator(dbContext),
            TimeProvider.System
        );
    }
}
