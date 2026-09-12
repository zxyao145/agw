using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Tests;

public partial class AgentflowRuntimeServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mermaid_ReadOnlyCompiler_RespectsTargetOwnershipWithoutRuntime(bool owned)
    {
        var id = Guid.CreateVersion7();
        var flow = new Agentflow
        {
            Id = id,
            Name = "diagram",
            CreateBy = "tester",
        };
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = id,
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
            },
            new AgentflowNode
            {
                AgentflowId = id,
                NodeId = "worker",
                Name = "Worker",
                Kind = AgentflowNodeKind.Agent,
                RelateId = Guid.CreateVersion7(),
            },
            new AgentflowNode
            {
                AgentflowId = id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = id,
                EdgeId = "first",
                SourceNodeId = "input",
                TargetNodeId = "worker",
            },
            new AgentflowEdge
            {
                AgentflowId = id,
                EdgeId = "last",
                SourceNodeId = "worker",
                TargetNodeId = "output",
            },
        };
        var definitions = new TestAgentflowDefinitionReader(
            new TestRepository<Agentflow>([flow], item => item.Id),
            new TestRepository<AgentflowNode>(nodes.ToList(), item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges.ToList(), item => (item.AgentflowId, item.EdgeId))
        );
        var provider = new AgentflowMermaidProvider(definitions, new DiagramCatalog(owned));
        var diagram = await provider.GetMermaidAsync(id, TestContext.Current.CancellationToken);
        if (!owned)
            Assert.Null(diagram);
        else
        {
            Assert.NotNull(diagram);
            Assert.Contains("worker", diagram, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class DiagramCatalog : IAgentCatalogFacade
    {
        private readonly bool _owned;

        public DiagramCatalog(bool owned)
        {
            _owned = owned;
        }

        public Task<bool> IsOwnedTargetAsync(
            AgentRuntimeType type,
            Guid id,
            string ownerUserId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(_owned);

        public Task<IReadOnlyList<AgentDescriptor>> ListDiscoverableAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<AgentDescriptor?> FindDiscoverableByNameAsync(
            string name,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlySet<Guid>> FilterExistingMcpServerIdsAsync(
            IReadOnlyCollection<Guid> serverIds,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
