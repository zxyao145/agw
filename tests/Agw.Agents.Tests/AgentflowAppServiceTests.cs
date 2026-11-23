using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.Definitions.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed partial class AgentflowAppServiceTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_ValidDefinition_CreatesRevisionOneUnpublished()
    {
        var userInfo = new TestUserInfoService();
        await using var connection = await OpenConnectionAsync();
        await using var dbContext = await CreateDbContextAsync(connection);
        var agent = await SeedAgentAsync(dbContext, "Runner", "tester");
        var service = CreateService(dbContext, userInfo);

        var created = await service.CreateAsync("review-flow", "Description", CreateDefinition(agent.Id));

        Assert.Equal(1, created.LatestRevision);
        Assert.Equal(0, created.PublishedRevision);
        Assert.Equal(1, await dbContext.AgentflowRevisions.CountAsync());
        Assert.All(await dbContext.AgentflowNodes.ToListAsync(), node => Assert.Equal(1, node.AgentflowRevision));
        Assert.All(await dbContext.AgentflowEdges.ToListAsync(), edge => Assert.Equal(1, edge.AgentflowRevision));
    }

    [Fact]
    public async Task SaveRevisionAsync_BaseRevisionIsLatest_AddsNextRevisionAndKeepsEarlierRows()
    {
        var userInfo = new TestUserInfoService();
        await using var connection = await OpenConnectionAsync();
        await using var dbContext = await CreateDbContextAsync(connection);
        var agent = await SeedAgentAsync(dbContext, "Runner", "tester");
        var service = CreateService(dbContext, userInfo);
        var created = await service.CreateAsync("review-flow", null, CreateDefinitionWithBlockMember(agent.Id));
        var earlierNodes = (await ReadNodesAsync(dbContext, created.Id, 1))
            .Select(node =>
                (
                    node.NodeId,
                    node.Kind,
                    node.RelateId,
                    node.Name,
                    node.PositionX,
                    node.PositionY,
                    node.RoutingStrategy,
                    node.SummaryModelProviderId,
                    node.Instructions,
                    node.ConfigJson
                )
            )
            .ToArray();
        var earlierEdges = (await ReadEdgesAsync(dbContext, created.Id, 1))
            .Select(edge =>
                (
                    edge.EdgeId,
                    edge.SourceNodeId,
                    edge.TargetNodeId,
                    edge.IsFanInBarrier,
                    edge.IsSwitchDefault,
                    edge.SwitchCaseOrder,
                    edge.ChoiceId,
                    edge.Label,
                    edge.ConditionJson
                )
            )
            .ToArray();
        var earlierMembers = (
            await dbContext
                .AgentflowBlockMembers.AsNoTracking()
                .Where(member => member.AgentflowId == created.Id && member.AgentflowRevision == 1)
                .ToListAsync()
        )
            .OrderBy(member => member.SortOrder)
            .Select(member =>
                (
                    member.NodeId,
                    member.MemberId,
                    member.Kind,
                    member.RelateId,
                    member.Name,
                    member.Instructions,
                    member.SortOrder,
                    member.IsManager
                )
            )
            .ToArray();

        var saved = await service.SaveRevisionAsync(created.Id, 1, "review-v2", "Updated", CreateDefinition(agent.Id));

        Assert.Equal(2, saved.LatestRevision);
        Assert.Equal(0, saved.PublishedRevision);
        Assert.Equal(2, await dbContext.AgentflowRevisions.CountAsync(row => row.AgentflowId == created.Id));
        Assert.Equal(
            earlierNodes,
            (await ReadNodesAsync(dbContext, created.Id, 1))
                .Select(node =>
                    (
                        node.NodeId,
                        node.Kind,
                        node.RelateId,
                        node.Name,
                        node.PositionX,
                        node.PositionY,
                        node.RoutingStrategy,
                        node.SummaryModelProviderId,
                        node.Instructions,
                        node.ConfigJson
                    )
                )
                .ToArray()
        );
        Assert.Equal(
            earlierEdges,
            (await ReadEdgesAsync(dbContext, created.Id, 1))
                .Select(edge =>
                    (
                        edge.EdgeId,
                        edge.SourceNodeId,
                        edge.TargetNodeId,
                        edge.IsFanInBarrier,
                        edge.IsSwitchDefault,
                        edge.SwitchCaseOrder,
                        edge.ChoiceId,
                        edge.Label,
                        edge.ConditionJson
                    )
                )
                .ToArray()
        );
        Assert.Equal(
            earlierMembers,
            (
                await dbContext
                    .AgentflowBlockMembers.AsNoTracking()
                    .Where(member => member.AgentflowId == created.Id && member.AgentflowRevision == 1)
                    .ToListAsync()
            )
                .OrderBy(member => member.SortOrder)
                .Select(member =>
                    (
                        member.NodeId,
                        member.MemberId,
                        member.Kind,
                        member.RelateId,
                        member.Name,
                        member.Instructions,
                        member.SortOrder,
                        member.IsManager
                    )
                )
                .ToArray()
        );
    }

    [Fact]
    public async Task SaveRevisionAsync_BaseRevisionIsStale_ThrowsRevisionConflict()
    {
        var userInfo = new TestUserInfoService();
        await using var connection = await OpenConnectionAsync();
        await using var dbContext = await CreateDbContextAsync(connection);
        var agent = await SeedAgentAsync(dbContext, "Runner", "tester");
        var service = CreateService(dbContext, userInfo);
        var created = await service.CreateAsync("review-flow", null, CreateDefinition(agent.Id));
        await service.SaveRevisionAsync(created.Id, 1, "review-v2", null, CreateDefinition(agent.Id));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.SaveRevisionAsync(created.Id, 1, "stale", null, CreateDefinition(agent.Id))
        );

        Assert.Equal(ErrorCodes.AgentflowRevisionConflict.Code, exception.Code);
        Assert.Equal(2, await dbContext.AgentflowRevisions.CountAsync(row => row.AgentflowId == created.Id));
    }

    [Fact]
    public async Task SaveRevisionAsync_DefinitionHasProblems_ThrowsDefinitionInvalidWithoutAddingRevision()
    {
        var userInfo = new TestUserInfoService();
        await using var connection = await OpenConnectionAsync();
        await using var dbContext = await CreateDbContextAsync(connection);
        var agent = await SeedAgentAsync(dbContext, "Runner", "tester");
        var service = CreateService(dbContext, userInfo);
        var created = await service.CreateAsync("review-flow", null, CreateDefinition(agent.Id));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.SaveRevisionAsync(created.Id, 1, " ", null, CreateDefinition(agent.Id))
        );

        Assert.Equal(ErrorCodes.AgentflowDefinitionInvalid.Code, exception.Code);
        Assert.Equal(1, await dbContext.AgentflowRevisions.CountAsync(row => row.AgentflowId == created.Id));
        Assert.Equal(1, (await dbContext.Agentflows.AsNoTracking().SingleAsync()).LatestRevision);
    }

    [Fact]
    public async Task PublishAsync_ExistingRevision_SetsPublishedRevision()
    {
        var userInfo = new TestUserInfoService();
        await using var connection = await OpenConnectionAsync();
        await using var dbContext = await CreateDbContextAsync(connection);
        var agent = await SeedAgentAsync(dbContext, "Runner", "tester");
        var service = CreateService(dbContext, userInfo);
        var created = await service.CreateAsync("review-flow", null, CreateDefinition(agent.Id));

        var published = await service.PublishAsync(created.Id, 1);

        Assert.Equal(1, published.PublishedRevision);
        Assert.Equal(1, (await dbContext.Agentflows.AsNoTracking().SingleAsync()).PublishedRevision);
    }

    [Theory]
    [InlineData(0, 4000001)]
    [InlineData(9, 4040018)]
    public async Task PublishAsync_RevisionZeroOrMissing_ThrowsExpectedError(int revision, int expectedCode)
    {
        var userInfo = new TestUserInfoService();
        await using var connection = await OpenConnectionAsync();
        await using var dbContext = await CreateDbContextAsync(connection);
        var agent = await SeedAgentAsync(dbContext, "Runner", "tester");
        var service = CreateService(dbContext, userInfo);
        var created = await service.CreateAsync("review-flow", null, CreateDefinition(agent.Id));

        var exception = await Assert.ThrowsAsync<AgwException>(() => service.PublishAsync(created.Id, revision));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task ListAsync_UnpublishedOrDisabled_IsExcluded()
    {
        var userInfo = new TestUserInfoService();
        await using var connection = await OpenConnectionAsync();
        await using var dbContext = await CreateDbContextAsync(connection);
        var agent = await SeedAgentAsync(dbContext, "Runner", "tester");
        var service = CreateService(dbContext, userInfo);
        var published = await service.CreateAsync("published", null, CreateDefinition(agent.Id));
        var disabled = await service.CreateAsync("disabled", null, CreateDefinition(agent.Id));
        var publishedDisabled = await service.CreateAsync("published-disabled", null, CreateDefinition(agent.Id));
        await service.PublishAsync(published.Id, 1);
        await service.PublishAsync(publishedDisabled.Id, 1);
        await service.UpdateEnabledAsync(disabled.Id, false);
        await service.UpdateEnabledAsync(publishedDisabled.Id, false);

        var listed = await service.ListAsync();

        Assert.Equal([published.Id], listed.Select(agentflow => agentflow.Id));
    }

    [Fact]
    public async Task PublishAsync_ReferenceIsNoLongerOwned_RejectsPublishing()
    {
        var userInfo = new TestUserInfoService();
        await using var connection = await OpenConnectionAsync();
        await using var dbContext = await CreateDbContextAsync(connection);
        var agent = await SeedAgentAsync(dbContext, "Runner", "tester");
        var service = CreateService(dbContext, userInfo);
        var created = await service.CreateAsync("review-flow", null, CreateDefinition(agent.Id));
        await dbContext
            .Agents.Where(row => row.Id == agent.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.CreateBy, "another-user"));
        dbContext.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<AgwException>(() => service.PublishAsync(created.Id, 1));

        Assert.Equal(ErrorCodes.AgentflowDefinitionInvalid.Code, exception.Code);
        Assert.Equal(
            0,
            (await dbContext.Agentflows.AsNoTracking().SingleAsync(row => row.Id == created.Id)).PublishedRevision
        );
    }

    [Fact]
    public async Task GetDetailAsync_ForeignAgentflow_ReturnsNotFound()
    {
        var userInfo = new TestUserInfoService();
        await using var connection = await OpenConnectionAsync();
        await using var dbContext = await CreateDbContextAsync(connection);
        var foreign = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "foreign",
            LatestRevision = 1,
            CreateBy = "someone-else",
            CreateTime = UtcNow,
        };
        dbContext.Agentflows.Add(foreign);
        dbContext.AgentflowRevisions.Add(
            new AgentflowRevision
            {
                AgentflowId = foreign.Id,
                Revision = 1,
                CreateBy = "someone-else",
                CreateTime = UtcNow,
            }
        );
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, userInfo);

        var exception = await Assert.ThrowsAsync<AgwException>(() => service.GetDetailAsync(foreign.Id, 1));

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task DeleteAsync_Agentflow_RemovesEveryRevisionRow()
    {
        var userInfo = new TestUserInfoService();
        await using var connection = await OpenConnectionAsync();
        await using var dbContext = await CreateDbContextAsync(connection);
        var agent = await SeedAgentAsync(dbContext, "Runner", "tester");
        var service = CreateService(dbContext, userInfo);
        var created = await service.CreateAsync("review-flow", null, CreateDefinition(agent.Id));
        await service.SaveRevisionAsync(created.Id, 1, "review-v2", null, CreateDefinition(agent.Id));

        var deleted = await service.DeleteAsync(created.Id);

        Assert.True(deleted);
        Assert.False(await dbContext.Agentflows.AnyAsync(row => row.Id == created.Id));
        Assert.False(await dbContext.AgentflowRevisions.AnyAsync(row => row.AgentflowId == created.Id));
        Assert.False(await dbContext.AgentflowNodes.AnyAsync(row => row.AgentflowId == created.Id));
        Assert.False(await dbContext.AgentflowEdges.AnyAsync(row => row.AgentflowId == created.Id));
        Assert.False(await dbContext.AgentflowBlockMembers.AnyAsync(row => row.AgentflowId == created.Id));
    }

    private static AgentflowAppService CreateService(
        AgwDbContext dbContext,
        TestUserInfoService userInfo,
        IApplicationLock? applicationLock = null
    ) =>
        new(
            dbContext,
            dbContext,
            new AgentflowDefinitionDomainService(
                new AgentDefinitionRepository(dbContext, userInfo),
                new TestModelProviderReferenceFacade(
                    new EfRepository<ModelProviderRelation>(dbContext),
                    new EfRepository<AgwAiModel>(dbContext),
                    new EfRepository<Provider>(dbContext),
                    userInfo
                )
            ),
            new AgentflowRevisionUsageReader(
                dbContext,
                new ConversationTurnStore(dbContext, new TestTimeProvider(UtcNow)),
                new AgentflowRevisionSnapshotReader(dbContext)
            ),
            new TestTimeProvider(UtcNow),
            userInfo,
            applicationLock
        );

    private static AgentflowDefinition CreateDefinition(Guid agentId)
    {
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                Name = "Input",
                RoutingStrategy = AgentflowRoutingStrategy.FanOut,
                ConfigJson = AgentflowDefinitionJson.EmptyOptions,
            },
            new AgentflowNode
            {
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                Name = "Runner",
                RelateId = agentId,
                RoutingStrategy = AgentflowRoutingStrategy.Direct,
                ConfigJson = AgentflowDefinitionJson.EmptyOptions,
            },
            new AgentflowNode
            {
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
                Name = "Output",
                RoutingStrategy = AgentflowRoutingStrategy.Direct,
                ConfigJson = AgentflowDefinitionJson.WriteOptions(new OutputOptions(false)),
            },
        };
        var edges = new[]
        {
            CreateEdge("input-agent", "input", "agent"),
            CreateEdge("agent-output", "agent", "output"),
        };
        return new AgentflowDefinition(nodes, edges, []);
    }

    private static AgentflowDefinition CreateDefinitionWithBlockMember(Guid agentId)
    {
        var definition = CreateDefinition(agentId);
        var nodes = definition.Nodes.Append(
            new AgentflowNode
            {
                NodeId = "concurrent",
                Kind = AgentflowNodeKind.ConcurrentBlock,
                Name = "Concurrent",
                RoutingStrategy = AgentflowRoutingStrategy.Direct,
                ConfigJson = AgentflowDefinitionJson.EmptyOptions,
            }
        );
        var edges = definition
            .Edges.Where(edge => edge.EdgeId != "agent-output")
            .Append(CreateEdge("agent-concurrent", "agent", "concurrent"))
            .Append(CreateEdge("concurrent-output", "concurrent", "output"))
            .ToArray();
        var members = new[]
        {
            new AgentflowBlockMember
            {
                NodeId = "concurrent",
                MemberId = "member-1",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
                Name = "Member one",
            },
        };

        return new AgentflowDefinition(nodes.ToArray(), edges, members);
    }

    private static AgentflowEdge CreateEdge(string edgeId, string sourceNodeId, string targetNodeId) =>
        new()
        {
            EdgeId = edgeId,
            SourceNodeId = sourceNodeId,
            TargetNodeId = targetNodeId,
            ConditionJson = AgentflowDefinitionJson.WriteConditions([]),
        };

    private static async Task<Agent> SeedAgentAsync(AgwDbContext dbContext, string name, string owner)
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            DisplayName = name,
            Type = AgentType.System,
            CreateBy = owner,
            CreateTime = UtcNow,
        };
        dbContext.Agents.Add(agent);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        return agent;
    }

    private static async Task<List<AgentflowNode>> ReadNodesAsync(
        AgwDbContext dbContext,
        Guid agentflowId,
        int revision
    ) =>
        await dbContext
            .AgentflowNodes.AsNoTracking()
            .Where(node => node.AgentflowId == agentflowId && node.AgentflowRevision == revision)
            .OrderBy(node => node.NodeId)
            .ToListAsync();

    private static async Task<List<AgentflowEdge>> ReadEdgesAsync(
        AgwDbContext dbContext,
        Guid agentflowId,
        int revision
    ) =>
        await dbContext
            .AgentflowEdges.AsNoTracking()
            .Where(edge => edge.AgentflowId == agentflowId && edge.AgentflowRevision == revision)
            .OrderBy(edge => edge.EdgeId)
            .ToListAsync();

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<AgwDbContext> CreateDbContextAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        return dbContext;
    }
}
