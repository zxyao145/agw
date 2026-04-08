using Agw.Agents.Domain.Services;
using Agw.Shared.Enums;

namespace Agw.Agents.Tests;

public class AgentflowDomainServiceTests
{
    private readonly AgentflowDomainService _service = new();

    [Fact]
    public void TryPrepareForCreate_BlankName_ReturnsFalse()
    {
        var agentflow = new Agentflow { Name = "  " };

        var result = _service.TryPrepareForCreate(agentflow, "tester");

        Assert.False(result);
        Assert.Equal(Guid.Empty, agentflow.Id);
        Assert.Null(agentflow.CreateBy);
    }

    [Fact]
    public void TryPrepareForCreate_ValidName_AssignsMetadata()
    {
        var before = DateTime.UtcNow;
        var agentflow = new Agentflow { Name = "workflow" };

        var result = _service.TryPrepareForCreate(agentflow, "tester");

        Assert.True(result);
        Assert.NotEqual(Guid.Empty, agentflow.Id);
        Assert.Equal("tester", agentflow.CreateBy);
        Assert.InRange(agentflow.CreateTime, before, DateTime.UtcNow);
    }

    [Fact]
    public void TryApplyUpdate_BlankNameAfterUpdate_ReturnsFalse()
    {
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "workflow" };

        var result = _service.TryApplyUpdate(agentflow, current => current.Name = "  ", "tester");

        Assert.False(result);
        Assert.Null(agentflow.UpdateBy);
        Assert.Null(agentflow.UpdateTime);
    }

    [Fact]
    public void TryApplyUpdate_ValidUpdate_SetsMetadata()
    {
        var before = DateTime.UtcNow;
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "workflow" };

        var result = _service.TryApplyUpdate(agentflow, current => current.Description = "updated", "tester");

        Assert.True(result);
        Assert.Equal("updated", agentflow.Description);
        Assert.Equal("tester", agentflow.UpdateBy);
        Assert.InRange(agentflow.UpdateTime!.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_NullCollections_ReturnsEmptyCollections()
    {
        var result = _service.ValidateAndNormalizeGraph(
            AgentflowOrchestrationPattern.Sequential,
            null,
            null,
            Guid.NewGuid(),
            []);

        Assert.Empty(result.Nodes!);
        Assert.Empty(result.Edges!);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_SequentialWithoutNodes_ReturnsNullCollections()
    {
        var result = _service.ValidateAndNormalizeGraph(
            AgentflowOrchestrationPattern.Sequential,
            [],
            [],
            Guid.NewGuid(),
            []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_MissingRelatedAgent_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Type = AgentflowNodeType.AgentNode, RelateId = Guid.NewGuid() },
        };

        var result = _service.ValidateAndNormalizeGraph(
            AgentflowOrchestrationPattern.Sequential,
            nodes,
            [],
            Guid.NewGuid(),
            []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_DuplicateEdgeIds_ReturnsNullCollections()
    {
        var agentId = Guid.NewGuid();
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Type = AgentflowNodeType.AgentNode, RelateId = agentId },
            new AgentflowNode { NodeId = "node-b", Type = AgentflowNodeType.AgentflowNode },
        };
        var edges = new[]
        {
            new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-a", TargetNodeId = "node-b" },
            new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-b", TargetNodeId = "node-a" },
        };

        var result = _service.ValidateAndNormalizeGraph(
            AgentflowOrchestrationPattern.Sequential,
            nodes,
            edges,
            Guid.NewGuid(),
            [agentId]);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_EdgeReferencesMissingNode_ReturnsNullCollections()
    {
        var agentId = Guid.NewGuid();
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Type = AgentflowNodeType.AgentNode, RelateId = agentId },
        };
        var edges = new[]
        {
            new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-a", TargetNodeId = "node-b" },
        };

        var result = _service.ValidateAndNormalizeGraph(
            AgentflowOrchestrationPattern.Sequential,
            nodes,
            edges,
            Guid.NewGuid(),
            [agentId]);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_ValidSequentialGraph_ReturnsNormalizedNodesAndEdges()
    {
        var agentflowId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Type = AgentflowNodeType.AgentNode, RelateId = agentId },
            new AgentflowNode { NodeId = "node-b", Type = AgentflowNodeType.AgentflowNode, RelateId = Guid.Empty },
        };
        var edges = new[]
        {
            new AgentflowEdge { EdgeId = "edge-a", SourceNodeId = "node-b", TargetNodeId = "node-a", Animated = false },
        };

        var (normalizedNodes, normalizedEdges) = _service.ValidateAndNormalizeGraph(
            AgentflowOrchestrationPattern.Sequential,
            nodes,
            edges,
            agentflowId,
            [agentId]);

        Assert.NotNull(normalizedNodes);
        Assert.NotNull(normalizedEdges);
        Assert.All(normalizedNodes!, node => Assert.Equal(agentflowId, node.AgentflowId));
        Assert.All(normalizedEdges!, edge => Assert.Equal(agentflowId, edge.AgentflowId));
        Assert.Equal(["node-a", "node-b"], normalizedNodes!.Select(node => node.NodeId));
        Assert.Equal("edge-a", normalizedEdges!.Single().EdgeId);
    }

    [Fact]
    public void OrderNodesByEdges_ConcurrentPattern_PreservesInputOrder()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-b" },
            new AgentflowNode { NodeId = "node-a" },
        };

        var result = _service.OrderNodesByEdges(
            nodes,
            [new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-a", TargetNodeId = "node-b" }],
            AgentflowOrchestrationPattern.Concurrent);

        Assert.Same(nodes, result);
    }

    [Fact]
    public void OrderNodesByEdges_AcyclicSequentialGraph_ReturnsTopologicallySortedNodes()
    {
        var first = new AgentflowNode { NodeId = "node-1" };
        var second = new AgentflowNode { NodeId = "node-2" };
        var third = new AgentflowNode { NodeId = "node-3" };

        var result = _service.OrderNodesByEdges(
            [third, second, first],
            [
                new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-1", TargetNodeId = "node-2" },
                new AgentflowEdge { EdgeId = "edge-2", SourceNodeId = "node-2", TargetNodeId = "node-3" },
            ],
            AgentflowOrchestrationPattern.Sequential);

        Assert.Equal(["node-1", "node-2", "node-3"], result.Select(node => node.NodeId));
    }

    [Fact]
    public void OrderNodesByEdges_CyclicGraph_FallsBackToOriginalOrder()
    {
        var original = new[]
        {
            new AgentflowNode { NodeId = "node-1" },
            new AgentflowNode { NodeId = "node-2" },
        };

        var result = _service.OrderNodesByEdges(
            original,
            [
                new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-1", TargetNodeId = "node-2" },
                new AgentflowEdge { EdgeId = "edge-2", SourceNodeId = "node-2", TargetNodeId = "node-1" },
            ],
            AgentflowOrchestrationPattern.Sequential);

        Assert.Equal(["node-1", "node-2"], result.Select(node => node.NodeId));
    }
}
