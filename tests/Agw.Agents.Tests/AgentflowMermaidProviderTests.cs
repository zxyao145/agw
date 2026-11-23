using Agw.Agents.Definitions.Facades;
using Agw.Agents.Definitions.Persistence;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public partial class AgentflowTurnExecutorTests
{
    [Fact]
    public async Task GetMermaidAsync_ExplicitRevision_RendersThatRevision()
    {
        using var database = new TestAgentDatabase();
        var id = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var now = DateTimeOffset.UnixEpoch;
        database.Context.Agentflows.Add(
            new Agentflow
            {
                Id = id,
                Name = "diagram",
                LatestRevision = 2,
                PublishedRevision = 1,
                CreateBy = "tester",
                CreateTime = now,
            }
        );
        database.Context.AgentflowRevisions.AddRange(
            new AgentflowRevision
            {
                AgentflowId = id,
                Revision = 1,
                CreateBy = "tester",
                CreateTime = now,
            },
            new AgentflowRevision
            {
                AgentflowId = id,
                Revision = 2,
                CreateBy = "tester",
                CreateTime = now,
            }
        );
        database.Context.Agents.Add(
            new Agent
            {
                Id = agentId,
                Name = "worker",
                DisplayName = "Worker",
                Type = AgentType.System,
                CreateBy = "tester",
                CreateTime = now,
            }
        );
        foreach (var revision in new[] { 1, 2 })
        {
            var workerNodeId = $"worker-{revision}";
            var workerName = revision == 1 ? "Revision One" : "Revision Two";
            database.Context.AgentflowNodes.AddRange(
                new AgentflowNode
                {
                    AgentflowId = id,
                    AgentflowRevision = revision,
                    CreateBy = "tester",
                    NodeId = "input",
                    Kind = AgentflowNodeKind.Input,
                },
                new AgentflowNode
                {
                    AgentflowId = id,
                    AgentflowRevision = revision,
                    CreateBy = "tester",
                    NodeId = workerNodeId,
                    Name = workerName,
                    Kind = AgentflowNodeKind.Agent,
                    RelateId = agentId,
                },
                new AgentflowNode
                {
                    AgentflowId = id,
                    AgentflowRevision = revision,
                    CreateBy = "tester",
                    NodeId = "output",
                    Kind = AgentflowNodeKind.Output,
                }
            );
            database.Context.AgentflowEdges.AddRange(
                new AgentflowEdge
                {
                    AgentflowId = id,
                    AgentflowRevision = revision,
                    CreateBy = "tester",
                    EdgeId = "first",
                    SourceNodeId = "input",
                    TargetNodeId = workerNodeId,
                },
                new AgentflowEdge
                {
                    AgentflowId = id,
                    AgentflowRevision = revision,
                    CreateBy = "tester",
                    EdgeId = "last",
                    SourceNodeId = workerNodeId,
                    TargetNodeId = "output",
                }
            );
        }
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var userInfo = new TestUserInfoService();
        var definitions = new AgentflowDefinitionReader(database.Context);
        var catalog = new AgentCatalogFacade(database.Context, userInfo);
        var provider = new AgentflowMermaidProvider(
            definitions,
            catalog,
            NullLogger<AgentflowWorkflowCompiler>.Instance
        );

        var diagram = await provider.GetMermaidAsync(id, 2, TestContext.Current.CancellationToken);

        Assert.NotNull(diagram);
        Assert.Contains("worker-2", diagram, StringComparison.Ordinal);
        Assert.DoesNotContain("worker-1", diagram, StringComparison.Ordinal);
    }
}
