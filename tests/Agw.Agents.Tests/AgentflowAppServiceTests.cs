using Agw.Agents.Definitions.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Providers;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed class AgentflowAppServiceTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_BlankName_ReturnsNullWithoutMutatingMetadata()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var service = CreateService(dbContext);
        var agentflow = new Agentflow { Name = "  " };

        // Act
        var result = await service.CreateAsync(agentflow, [], [], "tester");

        // Assert
        Assert.Null(result);
        Assert.Equal(Guid.Empty, agentflow.Id);
        Assert.Null(agentflow.CreateBy);
        Assert.Equal(default, agentflow.CreateTime);
        Assert.Empty(await dbContext.Agentflows.AsNoTracking().ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task CreateAsync_ValidGraph_AssignsMetadataAndPersistsOwnedChildren()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var service = CreateService(dbContext);
        var agentflow = new Agentflow { Name = "review-flow" };

        // Act
        var result = await service.CreateAsync(
            agentflow,
            [
                new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
                new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
            ],
            [Edge(Guid.Empty, "input-output", "input", "output")],
            "tester"
        );

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, agentflow.Id);
        Assert.Equal("tester", agentflow.CreateBy);
        Assert.Equal(UtcNow, agentflow.CreateTime);
        Assert.Equal(2, await dbContext.AgentflowNodes.CountAsync(cancellationToken));
        Assert.Single(await dbContext.AgentflowEdges.ToListAsync(cancellationToken));
        Assert.All(agentflow.Nodes, node => Assert.Equal(agentflow.Id, node.AgentflowId));
        Assert.All(agentflow.Edges, edge => Assert.Equal(agentflow.Id, edge.AgentflowId));
    }

    [Fact]
    public async Task UpdateAsync_ReplacingGraph_ReconcilesTrackedChildrenByKey()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var agentflowId = Guid.CreateVersion7();
        dbContext.Agentflows.Add(
            new Agentflow
            {
                Id = agentflowId,
                Name = "review-flow",
                CreateBy = "tester",
                CreateTime = UtcNow.AddDays(-1),
                Nodes =
                [
                    Node(agentflowId, "input", AgentflowNodeKind.Input),
                    Node(agentflowId, "worker", AgentflowNodeKind.PromptAdapter, name: "Old worker"),
                    Node(agentflowId, "removed", AgentflowNodeKind.Output),
                ],
                Edges =
                [
                    Edge(agentflowId, "input-worker", "input", "worker", label: "old label"),
                    Edge(agentflowId, "worker-removed", "worker", "removed"),
                ],
            }
        );
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        var service = CreateService(dbContext);

        // Act
        var result = await service.UpdateAsync(
            agentflowId,
            agentflow => agentflow.Description = "updated",
            [
                Node(agentflowId, "input", AgentflowNodeKind.Input),
                Node(agentflowId, "worker", AgentflowNodeKind.PromptAdapter, name: "Updated worker"),
                Node(agentflowId, "added", AgentflowNodeKind.Output),
            ],
            [
                Edge(agentflowId, "input-worker", "input", "worker", label: "updated label"),
                Edge(agentflowId, "worker-added", "worker", "added"),
            ],
            "updater"
        );

        // Assert
        Assert.NotNull(result);
        dbContext.ChangeTracker.Clear();
        var nodes = await dbContext
            .AgentflowNodes.AsNoTracking()
            .Where(node => node.AgentflowId == agentflowId)
            .OrderBy(node => node.NodeId)
            .ToListAsync(cancellationToken);
        var edges = await dbContext
            .AgentflowEdges.AsNoTracking()
            .Where(edge => edge.AgentflowId == agentflowId)
            .OrderBy(edge => edge.EdgeId)
            .ToListAsync(cancellationToken);
        Assert.Equal(["added", "input", "worker"], nodes.Select(node => node.NodeId));
        Assert.Equal("Updated worker", nodes.Single(node => node.NodeId == "worker").Name);
        Assert.Equal(["input-worker", "worker-added"], edges.Select(edge => edge.EdgeId));
        Assert.Equal("updated label", edges.Single(edge => edge.EdgeId == "input-worker").Label);
    }

    [Fact]
    public async Task UpdateAsync_BlankName_ReturnsNullWithoutPersistingChanges()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var agentflowId = Guid.CreateVersion7();
        dbContext.Agentflows.Add(
            new Agentflow
            {
                Id = agentflowId,
                Name = "review-flow",
                Description = "original",
                CreateBy = "tester",
                CreateTime = UtcNow,
                Nodes = [Node(agentflowId, "input", AgentflowNodeKind.Input)],
                Edges = [],
            }
        );
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        var service = CreateService(dbContext);

        // Act
        var result = await service.UpdateAsync(
            agentflowId,
            agentflow =>
            {
                agentflow.Name = " ";
                agentflow.Description = "changed";
            },
            [Node(agentflowId, "input", AgentflowNodeKind.Input)],
            [],
            "updater"
        );

        // Assert
        Assert.Null(result);
        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.Agentflows.AsNoTracking().SingleAsync(cancellationToken);
        Assert.Equal("review-flow", persisted.Name);
        Assert.Equal("original", persisted.Description);
    }

    private static AgentflowAppService CreateService(AgwDbContext dbContext) =>
        new(dbContext, new EfRepository<ModelProviderRelation>(dbContext), new TestTimeProvider(UtcNow));

    private static async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
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

    private static AgentflowNode Node(Guid agentflowId, string nodeId, AgentflowNodeKind kind, string? name = null) =>
        new()
        {
            AgentflowId = agentflowId,
            NodeId = nodeId,
            Kind = kind,
            Name = name,
            CreateBy = "tester",
            CreateTime = UtcNow.AddDays(-1),
        };

    private static AgentflowEdge Edge(
        Guid agentflowId,
        string edgeId,
        string sourceNodeId,
        string targetNodeId,
        string? label = null
    ) =>
        new()
        {
            AgentflowId = agentflowId,
            EdgeId = edgeId,
            SourceNodeId = sourceNodeId,
            TargetNodeId = targetNodeId,
            Label = label,
            CreateBy = "tester",
            CreateTime = UtcNow.AddDays(-1),
        };
}
