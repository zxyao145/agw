using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.Definitions.Persistence;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;

namespace Agw.Agents.Tests;

public sealed class AgentflowDefinitionDomainServiceTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidateAsync_MissingAgentReference_ReportsReferenceProblem()
    {
        using var database = new TestAgentDatabase();
        var userInfo = new TestUserInfoService();
        var definition = CreateDefinition(
            new AgentflowNode
            {
                NodeId = "runner",
                Kind = AgentflowNodeKind.Agent,
                Name = "Runner",
                RelateId = Guid.Empty,
                ConfigJson = AgentflowDefinitionJson.EmptyOptions,
            }
        );

        var problems = await CreateService(database.Context, userInfo)
            .ValidateAsync(null, "flow", definition, TestContext.Current.CancellationToken);

        Assert.Contains(problems, problem => problem.Code == "reference-missing" && problem.NodeId == "runner");
    }

    [Fact]
    public async Task ValidateAsync_InvalidBlockMemberKind_ReportsReferenceProblem()
    {
        using var database = new TestAgentDatabase();
        var userInfo = new TestUserInfoService();
        var definition = CreateDefinition(
            new AgentflowNode
            {
                NodeId = "team",
                Kind = AgentflowNodeKind.ConcurrentBlock,
                Name = "Team",
                ConfigJson = AgentflowDefinitionJson.EmptyOptions,
            }
        );
        definition = AgentflowDefinitionMapper.FromEntities(
            definition.Nodes,
            definition.Edges,
            [
                new AgentflowBlockMember
                {
                    NodeId = "team",
                    MemberId = "invalid",
                    Kind = AgentflowNodeKind.Input,
                    RelateId = Guid.CreateVersion7(),
                    Name = "Invalid member",
                },
            ]
        );

        var problems = await CreateService(database.Context, userInfo)
            .ValidateAsync(null, "flow", definition, TestContext.Current.CancellationToken);

        Assert.Contains(problems, problem => problem.Code == "reference-missing" && problem.NodeId == "team");
    }

    [Fact]
    public async Task ValidateAsync_UnpublishedNestedWorkflow_ReportsProblem()
    {
        using var database = new TestAgentDatabase();
        var userInfo = new TestUserInfoService();
        var nestedId = Guid.CreateVersion7();
        AddAgentflow(database.Context, nestedId, "nested", publishedRevision: 0, revisions: [1]);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var problems = await CreateService(database.Context, userInfo)
            .ValidateAsync(
                null,
                "flow",
                CreateDefinition(
                    new AgentflowNode
                    {
                        NodeId = "runner",
                        Kind = AgentflowNodeKind.WorkflowAsAgent,
                        Name = "Nested",
                        RelateId = nestedId,
                        ConfigJson = AgentflowDefinitionJson.EmptyOptions,
                    }
                ),
                TestContext.Current.CancellationToken
            );

        Assert.Contains(problems, problem => problem.Code == "nested-unpublished" && problem.NodeId == "runner");
    }

    [Fact]
    public async Task ValidateAsync_NestedReferenceCycle_ReportsProblem()
    {
        using var database = new TestAgentDatabase();
        var userInfo = new TestUserInfoService();
        var rootId = Guid.CreateVersion7();
        var nestedId = Guid.CreateVersion7();
        AddAgentflow(database.Context, rootId, "root", 1, 1);
        AddAgentflow(database.Context, nestedId, "nested", 1, 1);
        database.Context.AgentflowNodes.Add(
            new AgentflowNode
            {
                AgentflowId = nestedId,
                AgentflowRevision = 1,
                NodeId = "back-to-root",
                Kind = AgentflowNodeKind.WorkflowAsAgent,
                RelateId = rootId,
                CreateBy = "tester",
                CreateTime = UtcNow,
            }
        );
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var problems = await CreateService(database.Context, userInfo)
            .ValidateAsync(
                rootId,
                "root",
                CreateDefinition(
                    new AgentflowNode
                    {
                        NodeId = "nested",
                        Kind = AgentflowNodeKind.WorkflowAsAgent,
                        Name = "Nested",
                        RelateId = nestedId,
                        ConfigJson = AgentflowDefinitionJson.EmptyOptions,
                    }
                ),
                TestContext.Current.CancellationToken
            );

        Assert.Contains(problems, problem => problem.Code == "nested-cycle" && problem.NodeId == "nested");
    }

    [Fact]
    public async Task ValidateAsync_SummaryWithoutModelProvider_ReportsProblem()
    {
        using var database = new TestAgentDatabase();
        var userInfo = new TestUserInfoService();
        var definition = CreateDefinition(
            new AgentflowNode
            {
                NodeId = "runner",
                Kind = AgentflowNodeKind.Agent,
                Name = "Runner",
                RelateId = Guid.CreateVersion7(),
                ConfigJson = AgentflowDefinitionJson.EmptyOptions,
            }
        );
        var output = definition.Nodes.Single(node => node.Kind == AgentflowNodeKind.Output);
        output.ConfigJson = AgentflowDefinitionJson.WriteOptions(new OutputOptions(true));
        definition = AgentflowDefinitionMapper.FromEntities(definition.Nodes, definition.Edges, definition.Members);

        var problems = await CreateService(database.Context, userInfo)
            .ValidateAsync(null, "flow", definition, TestContext.Current.CancellationToken);

        Assert.Contains(problems, problem => problem.Code == "summary-model-missing" && problem.NodeId == "output");
    }

    [Fact]
    public async Task ValidateAsync_ResultConditionOnUnstructuredAgent_ReportsProblem()
    {
        using var database = new TestAgentDatabase();
        var userInfo = new TestUserInfoService();
        var agent = await SeedAgentAsync(database.Context, "Runner", structured: false);
        var definition = CreateDefinition(
            new AgentflowNode
            {
                NodeId = "runner",
                Kind = AgentflowNodeKind.Agent,
                Name = "Runner",
                RelateId = agent.Id,
                ConfigJson = AgentflowDefinitionJson.EmptyOptions,
            }
        );
        definition.Edges.Single(edge => edge.EdgeId == "runner-output").ConditionJson =
            AgentflowDefinitionJson.WriteConditions([new AgentflowConditionRule("result", "approved", "status")]);
        definition = AgentflowDefinitionMapper.FromEntities(definition.Nodes, definition.Edges, definition.Members);

        var problems = await CreateService(database.Context, userInfo)
            .ValidateAsync(null, "flow", definition, TestContext.Current.CancellationToken);

        Assert.Contains(
            problems,
            problem => problem.Code == "condition-result-unavailable" && problem.EdgeId == "runner-output"
        );
    }

    [Fact]
    public async Task ValidateAsync_ResultRuleOnStructuredAgent_ReportsNothing()
    {
        using var database = new TestAgentDatabase();
        var userInfo = new TestUserInfoService();
        var agent = await SeedAgentAsync(database.Context, "Runner", structured: true);
        var definition = CreateDefinition(
            new AgentflowNode
            {
                NodeId = "runner",
                Kind = AgentflowNodeKind.Agent,
                Name = "Runner",
                RelateId = agent.Id,
                ConfigJson = AgentflowDefinitionJson.EmptyOptions,
            }
        );
        definition.Edges.Single(edge => edge.EdgeId == "runner-output").ConditionJson =
            AgentflowDefinitionJson.WriteConditions([new AgentflowConditionRule("result", "approved", "status")]);
        definition = AgentflowDefinitionMapper.FromEntities(definition.Nodes, definition.Edges, definition.Members);

        var problems = await CreateService(database.Context, userInfo)
            .ValidateAsync(null, "flow", definition, TestContext.Current.CancellationToken);

        Assert.Empty(problems);
    }

    [Fact]
    public async Task ValidateAsync_AgentflowIdNull_DoesNotTreatNewDefinitionAsSelfReference()
    {
        using var database = new TestAgentDatabase();
        var userInfo = new TestUserInfoService();
        var agent = await SeedAgentAsync(database.Context, "Runner", structured: false);

        var problems = await CreateService(database.Context, userInfo)
            .ValidateAsync(
                null,
                "flow",
                CreateDefinition(
                    new AgentflowNode
                    {
                        NodeId = "runner",
                        Kind = AgentflowNodeKind.Agent,
                        Name = "Runner",
                        RelateId = agent.Id,
                        ConfigJson = AgentflowDefinitionJson.EmptyOptions,
                    }
                ),
                TestContext.Current.CancellationToken
            );

        Assert.DoesNotContain(problems, problem => problem.Code == "nested-cycle");
    }

    private static AgentflowDefinitionDomainService CreateService(
        AgwDbContext dbContext,
        TestUserInfoService userInfo
    ) =>
        new(
            new AgentDefinitionRepository(dbContext, userInfo),
            new TestModelProviderReferenceFacade(
                new EfRepository<ModelProviderRelation>(dbContext),
                new EfRepository<AgwAiModel>(dbContext),
                new EfRepository<Provider>(dbContext),
                userInfo
            )
        );

    private static AgentflowDefinition CreateDefinition(AgentflowNode runner)
    {
        var input = new AgentflowNode
        {
            NodeId = "input",
            Kind = AgentflowNodeKind.Input,
            Name = "Input",
            RoutingStrategy = AgentflowRoutingStrategy.FanOut,
            ConfigJson = AgentflowDefinitionJson.EmptyOptions,
        };
        var output = new AgentflowNode
        {
            NodeId = "output",
            Kind = AgentflowNodeKind.Output,
            Name = "Output",
            ConfigJson = AgentflowDefinitionJson.WriteOptions(new OutputOptions(false)),
        };
        var inputEdge = new AgentflowEdge
        {
            EdgeId = "input-runner",
            SourceNodeId = "input",
            TargetNodeId = runner.NodeId,
            ConditionJson = AgentflowDefinitionJson.WriteConditions([]),
        };
        var outputEdge = new AgentflowEdge
        {
            EdgeId = "runner-output",
            SourceNodeId = runner.NodeId,
            TargetNodeId = "output",
            ConditionJson = AgentflowDefinitionJson.WriteConditions([]),
        };
        return AgentflowDefinitionMapper.FromEntities([input, runner, output], [inputEdge, outputEdge], []);
    }

    private static void AddAgentflow(
        AgwDbContext dbContext,
        Guid id,
        string name,
        int publishedRevision,
        params int[] revisions
    )
    {
        dbContext.Agentflows.Add(
            new Agentflow
            {
                Id = id,
                Name = name,
                LatestRevision = revisions.Max(),
                PublishedRevision = publishedRevision,
                CreateBy = "tester",
                CreateTime = UtcNow,
            }
        );
        dbContext.AgentflowRevisions.AddRange(
            revisions.Select(revision => new AgentflowRevision
            {
                AgentflowId = id,
                Revision = revision,
                CreateBy = "tester",
                CreateTime = UtcNow,
            })
        );
    }

    private static async Task<Agent> SeedAgentAsync(AgwDbContext dbContext, string name, bool structured)
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            DisplayName = name,
            Type = AgentType.System,
            EnableSummary = structured,
            ResponseSchema = structured ? "{}" : null,
            CreateBy = "tester",
            CreateTime = UtcNow,
        };
        dbContext.Agents.Add(agent);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return agent;
    }
}
