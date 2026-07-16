using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Projects.Application;
using Agw.Projects.Domain.Services;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Skills;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace Agw.Host.Tests;

public class ProjectTraceCleanupTests
{
    [Fact]
    public async Task ClearRecordsAsync_ContextHasTraces_RemovesOnlyContextTraces()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var projectId = Guid.CreateVersion7();
        await SeedProjectContextsAndTracesAsync(dbContext, projectId, cancellationToken);
        var service = CreateProjectContextService(dbContext);

        await service.ClearRecordsAsync(projectId, "context-1");

        var trace = Assert.Single(await dbContext.AgentflowNodeExecutionTraces.ToListAsync(cancellationToken));
        Assert.Equal("context-2", trace.ContextId);
    }

    [Fact]
    public async Task DeleteAsync_ContextHasTraces_RemovesOnlyContextTraces()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var projectId = Guid.CreateVersion7();
        await SeedProjectContextsAndTracesAsync(dbContext, projectId, cancellationToken);
        var service = CreateProjectContextService(dbContext);

        await service.DeleteAsync(projectId, "context-1");

        var trace = Assert.Single(await dbContext.AgentflowNodeExecutionTraces.ToListAsync(cancellationToken));
        Assert.Equal("context-2", trace.ContextId);
    }

    [Fact]
    public async Task DeleteAllAsync_ProjectHasTraces_RemovesOnlyProjectTraces()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var projectId = Guid.CreateVersion7();
        var otherProjectId = Guid.CreateVersion7();
        await SeedProjectContextsAndTracesAsync(dbContext, projectId, cancellationToken);
        await SeedProjectContextsAndTracesAsync(dbContext, otherProjectId, cancellationToken, "other-");
        var service = CreateProjectContextService(dbContext);

        await service.DeleteAllAsync(projectId);

        var traces = await dbContext.AgentflowNodeExecutionTraces.ToListAsync(cancellationToken);
        Assert.Equal(2, traces.Count);
        Assert.All(traces, trace => Assert.Equal(otherProjectId, trace.ProjectId));
    }

    [Fact]
    public async Task DeleteAsync_ProjectHasTraces_RemovesOnlyProjectTraces()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var projectId = Guid.CreateVersion7();
        var otherProjectId = Guid.CreateVersion7();
        await SeedProjectContextsAndTracesAsync(dbContext, projectId, cancellationToken);
        await SeedProjectContextsAndTracesAsync(dbContext, otherProjectId, cancellationToken, "other-");
        var service = CreateProjectService(dbContext);

        await service.DeleteAsync(projectId);

        var traces = await dbContext.AgentflowNodeExecutionTraces.ToListAsync(cancellationToken);
        Assert.Equal(2, traces.Count);
        Assert.All(traces, trace => Assert.Equal(otherProjectId, trace.ProjectId));
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

    private static async Task SeedProjectContextsAndTracesAsync(
        AgwDbContext dbContext,
        Guid projectId,
        CancellationToken cancellationToken,
        string contextPrefix = "")
    {
        dbContext.Projects.Add(new Project
        {
            Id = projectId,
            Name = $"Project-{projectId:N}",
            Type = ProjectType.UserDefined,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        });
        dbContext.ProjectContexts.AddRange(
            CreateProjectContext(projectId, $"{contextPrefix}context-1"),
            CreateProjectContext(projectId, $"{contextPrefix}context-2"));
        dbContext.AgentflowNodeExecutionTraces.AddRange(
            CreateTrace(projectId, $"{contextPrefix}context-1"),
            CreateTrace(projectId, $"{contextPrefix}context-2"));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ProjectContext CreateProjectContext(Guid projectId, string contextId) => new()
    {
        Id = Guid.CreateVersion7(),
        ProjectId = projectId,
        ContextId = contextId,
        Title = contextId,
        CreateBy = "tester",
        CreateTime = TimeProvider.System.GetUtcNow(),
    };

    private static AgentflowTrace CreateTrace(Guid projectId, string contextId) => new()
    {
        Id = Guid.CreateVersion7(),
        StartTimeUtc = TimeProvider.System.GetUtcNow(),
        ProjectId = projectId,
        ContextId = contextId,
        TaskId = Guid.CreateVersion7(),
        AgentflowId = Guid.CreateVersion7(),
        NodeId = "node-1",
        NodeKind = AgentflowNodeKind.Agent,
        Input = "input",
        Status = AgentflowNodeExecutionStatus.Succeeded,
    };

    private static ProjectContextAppService CreateProjectContextService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);
        return new ProjectContextAppService(
            new EfRepository<ProjectContext>(dbContext),
            new EfRepository<TaskRecord>(dbContext),
            new EfRepository<AgentflowTrace>(dbContext),
            new EfRepository<AgentUsage>(dbContext),
            new UnitOfWork(dbContext),
            new ProjectResolver(projectRepository),
            new TaskRecordDomainService(),
            new TaskSessionBindingService(
                new EfRepository<TaskSessionBinding>(dbContext),
                new EfRepository<ProjectContext>(dbContext),
                new UnitOfWork(dbContext),
                TimeProvider.System),
            TimeProvider.System);
    }

    private static ProjectAppService CreateProjectService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);
        return new ProjectAppService(
            projectRepository,
            new EfRepository<ProjectMcpServerRelation>(dbContext),
            new EfRepository<McpServer>(dbContext),
            new EfRepository<ProjectSkillRelation>(dbContext),
            new EfRepository<Skill>(dbContext),
            new EfRepository<ProjectConnectionRelation>(dbContext),
            new EfRepository<Connection>(dbContext),
            new EfRepository<AgentflowTrace>(dbContext),
            new UnitOfWork(dbContext),
            new ProjectDomainService(TimeProvider.System),
            new ProjectResolver(projectRepository));
    }
}
