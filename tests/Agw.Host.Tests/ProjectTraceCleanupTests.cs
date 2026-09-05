using Agw.Agents.Definitions.Facades;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Application.Facades;
using Agw.Projects.Application;
using Agw.Projects.Domain.Services;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Skills.Application.Facades;
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
        await SeedProjectConversationsAndTracesAsync(dbContext, projectId, cancellationToken);
        var service = CreateProjectConversationService(dbContext);
        var conversationId = await dbContext
            .ProjectConversations.Where(conversation =>
                conversation.ProjectId == projectId && conversation.ContextId == "context-1"
            )
            .Select(conversation => conversation.Id)
            .SingleAsync(cancellationToken);

        await service.ClearRecordsAsync(projectId, conversationId);

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
        await SeedProjectConversationsAndTracesAsync(dbContext, projectId, cancellationToken);
        var service = CreateProjectConversationService(dbContext);
        var conversationId = await dbContext
            .ProjectConversations.Where(conversation =>
                conversation.ProjectId == projectId && conversation.ContextId == "context-1"
            )
            .Select(conversation => conversation.Id)
            .SingleAsync(cancellationToken);

        await service.DeleteAsync(projectId, conversationId);

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
        await SeedProjectConversationsAndTracesAsync(dbContext, projectId, cancellationToken);
        await SeedProjectConversationsAndTracesAsync(dbContext, otherProjectId, cancellationToken, "other-");
        var service = CreateProjectConversationService(dbContext);

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
        await SeedProjectConversationsAndTracesAsync(dbContext, projectId, cancellationToken);
        await SeedProjectConversationsAndTracesAsync(dbContext, otherProjectId, cancellationToken, "other-");
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
        CancellationToken cancellationToken
    )
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        return dbContext;
    }

    private static async Task SeedProjectConversationsAndTracesAsync(
        AgwDbContext dbContext,
        Guid projectId,
        CancellationToken cancellationToken,
        string contextPrefix = ""
    )
    {
        dbContext.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = $"Project-{projectId:N}",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow(),
            }
        );
        dbContext.ProjectConversations.AddRange(
            CreateProjectConversation(projectId, $"{contextPrefix}context-1"),
            CreateProjectConversation(projectId, $"{contextPrefix}context-2")
        );
        dbContext.AgentflowNodeExecutionTraces.AddRange(
            CreateTrace(projectId, $"{contextPrefix}context-1"),
            CreateTrace(projectId, $"{contextPrefix}context-2")
        );
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ProjectConversation CreateProjectConversation(Guid projectId, string contextId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            ContextId = contextId,
            Title = contextId,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static AgentflowTrace CreateTrace(Guid projectId, string contextId) =>
        new()
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

    private static ProjectConversationAppService CreateProjectConversationService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);
        var userInfo = new TestUserInfoService();
        return new ProjectConversationAppService(
            new EfRepository<ProjectConversation>(dbContext),
            new EfRepository<ProjectConversationChatHistory>(dbContext),
            new EfRepository<AgentUsage>(dbContext),
            dbContext,
            new ProjectResolver(projectRepository, userInfo),
            new ProjectDeletionCoordinator(
                dbContext,
                Agw.Shared.Coordination.InMemoryApplicationLock.Shared,
                new Agw.Infrastructure.Agents.DurableExecutionScopeMaintenance(
                    dbContext,
                    Agw.Shared.Coordination.InMemoryApplicationLock.Shared,
                    TimeProvider.System,
                    Microsoft
                        .Extensions
                        .Logging
                        .Abstractions
                        .NullLogger<Agw.Infrastructure.Agents.DurableExecutionScopeMaintenance>
                        .Instance
                )
            ),
            TimeProvider.System
        );
    }

    private static ProjectAppService CreateProjectService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);
        var userInfo = new TestUserInfoService();
        return new ProjectAppService(
            projectRepository,
            new EfRepository<ProjectMcpServerRelation>(dbContext),
            new AgentCatalogFacade(
                new EfRepository<Agent>(dbContext),
                new EfRepository<Agentflow>(dbContext),
                new EfRepository<McpServer>(dbContext),
                new EfRepository<AgentSkillRelation>(dbContext),
                dbContext,
                userInfo
            ),
            new EfRepository<ProjectSkillRelation>(dbContext),
            new SkillReferenceFacade(dbContext, userInfo),
            new EfRepository<ProjectConnectionRelation>(dbContext),
            new ConnectionReferenceFacade(dbContext, userInfo),
            new ProjectDeletionCoordinator(
                dbContext,
                Agw.Shared.Coordination.InMemoryApplicationLock.Shared,
                new Agw.Infrastructure.Agents.DurableExecutionScopeMaintenance(
                    dbContext,
                    Agw.Shared.Coordination.InMemoryApplicationLock.Shared,
                    TimeProvider.System,
                    Microsoft
                        .Extensions
                        .Logging
                        .Abstractions
                        .NullLogger<Agw.Infrastructure.Agents.DurableExecutionScopeMaintenance>
                        .Instance
                )
            ),
            dbContext,
            new ProjectDomainService(TimeProvider.System),
            new ProjectResolver(projectRepository, userInfo),
            userInfo
        );
    }
}
