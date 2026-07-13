using Agw.Host.Controllers;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Jobs.Domain.Entities;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Tasks;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace Agw.Host.Tests;

public class DashboardControllerTests
{
    [Fact]
    public async Task GetStats_MultipleProjectContexts_SumsAllTokenUsage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var projectId = Guid.NewGuid();
        dbContext.Projects.Add(CreateProject(projectId));
        dbContext.ProjectContexts.AddRange(
            CreateProjectContext(projectId, "context-1", 10, 20, 30),
            CreateProjectContext(projectId, "context-2", 5, 7, 12));
        await dbContext.SaveChangesAsync(cancellationToken);
        var controller = CreateController(dbContext);

        var result = await controller.GetStats();

        var stats = ReadStats(result);
        Assert.Equal(15L, ReadLongProperty(stats, "UsageInputTokenCount"));
        Assert.Equal(27L, ReadLongProperty(stats, "UsageOutputTokenCount"));
        Assert.Equal(42L, ReadLongProperty(stats, "UsageTotalTokenCount"));
    }

    [Fact]
    public async Task GetStats_NoProjectContexts_ReturnsZeroTokenUsage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var controller = CreateController(dbContext);

        var result = await controller.GetStats();

        var stats = ReadStats(result);
        Assert.Equal(0L, ReadLongProperty(stats, "UsageInputTokenCount"));
        Assert.Equal(0L, ReadLongProperty(stats, "UsageOutputTokenCount"));
        Assert.Equal(0L, ReadLongProperty(stats, "UsageTotalTokenCount"));
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<AgwDbContext> CreateDbContextAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        return dbContext;
    }

    private static DashboardController CreateController(AgwDbContext dbContext)
    {
        return new DashboardController(
            new EfRepository<Job>(dbContext),
            new EfRepository<Project>(dbContext),
            new EfRepository<ProjectContext>(dbContext),
            new EfRepository<TaskRecord>(dbContext),
            new EfRepository<Agent>(dbContext),
            new EfRepository<Agentflow>(dbContext));
    }

    private static Project CreateProject(Guid projectId) => new()
    {
        Id = projectId,
        Name = $"Project-{projectId:N}",
        Type = ProjectType.UserDefined,
        Enable = true,
        CreateBy = "tester",
        CreateTime = TimeProvider.System.GetUtcNow(),
    };

    private static ProjectContext CreateProjectContext(
        Guid projectId,
        string contextId,
        long inputTokenCount,
        long outputTokenCount,
        long totalTokenCount) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        ContextId = contextId,
        Title = contextId,
        Usage = new ProjectContextUsage
        {
            InputTokenCount = inputTokenCount,
            OutputTokenCount = outputTokenCount,
            TotalTokenCount = totalTokenCount,
        },
        CreateBy = "tester",
        CreateTime = TimeProvider.System.GetUtcNow(),
    };

    private static DashboardStatsResponse ReadStats(IActionResult result)
    {
        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
        var dataProperty = result.GetType().GetProperty("Data");
        Assert.NotNull(dataProperty);
        return Assert.IsType<DashboardStatsResponse>(dataProperty!.GetValue(result));
    }

    private static long ReadLongProperty(DashboardStatsResponse stats, string propertyName)
    {
        var property = typeof(DashboardStatsResponse).GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<long>(property!.GetValue(stats));
    }
}
