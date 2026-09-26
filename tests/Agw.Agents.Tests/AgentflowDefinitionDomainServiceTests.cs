using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Agents.Definitions.Persistence;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed class AgentflowDefinitionDomainServiceTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryDefineGraphAsync_OwnedAgentWithBlankNodeName_DefaultsNodeNameToAgentName()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(token);
        await using var dbContext = await CreateDbContextAsync(connection, token);
        var agent = await SeedAgentAsync(dbContext, "reviewer", "tester", token);
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "flow" };
        var (nodes, edges) = CreateAgentGraph(agent.Id, AgentflowNodeKind.Agent);

        // Act
        var defined = await CreateService(dbContext)
            .TryDefineGraphAsync(agentflow, nodes, edges, AgentflowConfigurationParser.Parse(nodes, edges), token);

        // Assert
        Assert.True(defined);
        Assert.Equal("reviewer", agentflow.Nodes.Single(node => node.NodeId == "target").Name);
    }

    [Fact]
    public async Task TryDefineGraphAsync_AgentOwnedByAnotherUser_ReturnsFalseWithoutChange()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(token);
        await using var dbContext = await CreateDbContextAsync(connection, token);
        var foreignAgent = await SeedAgentAsync(dbContext, "foreign", "someone-else", token);
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "flow" };
        var (nodes, edges) = CreateAgentGraph(foreignAgent.Id, AgentflowNodeKind.Agent);

        // Act
        var defined = await CreateService(dbContext)
            .TryDefineGraphAsync(agentflow, nodes, edges, AgentflowConfigurationParser.Parse(nodes, edges), token);

        // Assert
        Assert.False(defined);
        Assert.Empty(agentflow.Nodes);
    }

    [Fact]
    public async Task TryDefineGraphAsync_MissingReferencedAgent_ReturnsFalse()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(token);
        await using var dbContext = await CreateDbContextAsync(connection, token);
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), Name = "flow" };
        var (nodes, edges) = CreateAgentGraph(Guid.CreateVersion7(), AgentflowNodeKind.Agent);

        // Act
        var defined = await CreateService(dbContext)
            .TryDefineGraphAsync(agentflow, nodes, edges, AgentflowConfigurationParser.Parse(nodes, edges), token);

        // Assert
        Assert.False(defined);
    }

    [Fact]
    public async Task TryDefineGraphAsync_SelfReference_ReturnsFalse()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(token);
        await using var dbContext = await CreateDbContextAsync(connection, token);
        var agentflow = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "flow",
            CreateBy = "tester",
            CreateTime = UtcNow,
        };
        dbContext.Agentflows.Add(agentflow);
        await dbContext.SaveChangesAsync(token);
        var (nodes, edges) = CreateAgentGraph(agentflow.Id, AgentflowNodeKind.WorkflowAsAgent);

        // Act
        var defined = await CreateService(dbContext)
            .TryDefineGraphAsync(agentflow, nodes, edges, AgentflowConfigurationParser.Parse(nodes, edges), token);

        // Assert
        Assert.False(defined);
    }

    [Fact]
    public async Task TryDefineGraphAsync_SummaryWithInvisibleModelProvider_ReturnsFalse()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(token);
        await using var dbContext = await CreateDbContextAsync(connection, token);
        var agentflow = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "flow",
            SummaryModelProviderId = Guid.CreateVersion7(),
        };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode
            {
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
                ConfigJson = """{"enableSummary":true}""",
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "input-output",
                SourceNodeId = "input",
                TargetNodeId = "output",
            },
        };

        // Act
        var defined = await CreateService(dbContext)
            .TryDefineGraphAsync(agentflow, nodes, edges, AgentflowConfigurationParser.Parse(nodes, edges), token);

        // Assert
        Assert.False(defined);
    }

    private static AgentflowDefinitionDomainService CreateService(AgwDbContext dbContext)
    {
        var userInfo = new TestUserInfoService();
        return new AgentflowDefinitionDomainService(
            new AgentDefinitionRepository(dbContext, userInfo),
            new TestModelProviderReferenceFacade(
                new EfRepository<ModelProviderRelation>(dbContext),
                new EfRepository<AgwAiModel>(dbContext),
                new EfRepository<Provider>(dbContext),
                userInfo
            )
        );
    }

    private static (AgentflowNode[] Nodes, AgentflowEdge[] Edges) CreateAgentGraph(
        Guid targetId,
        AgentflowNodeKind targetKind
    ) =>
        (
            [
                new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
                new AgentflowNode
                {
                    NodeId = "target",
                    Kind = targetKind,
                    RelateId = targetId,
                    Name = " ",
                },
            ],
            [
                new AgentflowEdge
                {
                    EdgeId = "input-target",
                    SourceNodeId = "input",
                    TargetNodeId = "target",
                },
            ]
        );

    private static async Task<Agent> SeedAgentAsync(
        AgwDbContext dbContext,
        string name,
        string owner,
        CancellationToken cancellationToken
    )
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Type = AgentType.System,
            CreateBy = owner,
            CreateTime = UtcNow,
        };
        dbContext.Agents.Add(agent);
        await dbContext.SaveChangesAsync(cancellationToken);
        return agent;
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
}
