using Agw.Host.Controllers;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Jobs.Domain.Entities;
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
    public async Task GetStats_MultipleAgentUsages_SumsAllTokenUsage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var projectId = Guid.NewGuid();
        dbContext.AgentUsages.AddRange(
            CreateAgentUsage(projectId, "context-1", 10, 20, 30),
            CreateAgentUsage(projectId, "context-2", 5, 7, 12));
        await dbContext.SaveChangesAsync(cancellationToken);
        var controller = CreateController(dbContext);

        var result = await controller.GetStats();

        var stats = ReadStats(result);
        Assert.Equal(15L, ReadLongProperty(stats, "UsageInputTokenCount"));
        Assert.Equal(27L, ReadLongProperty(stats, "UsageOutputTokenCount"));
        Assert.Equal(42L, ReadLongProperty(stats, "UsageTotalTokenCount"));
    }

    [Fact]
    public async Task GetStats_NoAgentUsages_ReturnsZeroTokenUsage()
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

    [Fact]
    public async Task GetStats_ProjectDeleted_IncludesRetainedUsage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var projectId = Guid.NewGuid();
        dbContext.Projects.Add(new Project
        {
            Id = projectId,
            Name = "Deleted project",
            CreateTime = TimeProvider.System.GetUtcNow()
        });
        dbContext.ProjectContexts.Add(new ProjectContext
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ContextId = "context-1",
            Title = "Context",
            CreateTime = TimeProvider.System.GetUtcNow()
        });
        dbContext.AgentUsages.Add(CreateAgentUsage(projectId, "context-1", 10, 20, 30));
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.Projects.Remove(await dbContext.Projects.SingleAsync(cancellationToken));
        await dbContext.SaveChangesAsync(cancellationToken);

        Assert.Empty(await dbContext.ProjectContexts.ToListAsync(cancellationToken));
        Assert.Single(await dbContext.AgentUsages.ToListAsync(cancellationToken));
        var stats = ReadStats(await CreateController(dbContext).GetStats());
        Assert.Equal(30L, ReadLongProperty(stats, "UsageTotalTokenCount"));
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
            new EfRepository<AgentUsage>(dbContext),
            new EfRepository<TaskRecord>(dbContext),
            new EfRepository<Agent>(dbContext),
            new EfRepository<Agentflow>(dbContext));
    }

    private static AgentUsage CreateAgentUsage(
        Guid projectId,
        string contextId,
        long inputTokenCount,
        long outputTokenCount,
        long totalTokenCount) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        ContextId = contextId,
        AgentName = "planner",
        RecordedAt = TimeProvider.System.GetUtcNow(),
        InputTokenCount = inputTokenCount,
        OutputTokenCount = outputTokenCount,
        TotalTokenCount = totalTokenCount
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
