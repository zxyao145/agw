using Agw.Shared.Data.Entities.Agentflows;
using Agw.Testing;

namespace Agw.Agents.Tests;

public class AgentflowDomainServiceTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly AgentflowDomainService _service = new(new TestTimeProvider(UtcNow));

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
        var agentflow = new Agentflow { Name = "workflow" };

        var result = _service.TryPrepareForCreate(agentflow, "tester");

        Assert.True(result);
        Assert.NotEqual(Guid.Empty, agentflow.Id);
        Assert.Equal("tester", agentflow.CreateBy);
        Assert.Equal(UtcNow, agentflow.CreateTime);
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
        var agentflow = new Agentflow { Id = Guid.NewGuid(), Name = "workflow" };

        var result = _service.TryApplyUpdate(agentflow, current => current.Description = "updated", "tester");

        Assert.True(result);
        Assert.Equal("updated", agentflow.Description);
        Assert.Equal("tester", agentflow.UpdateBy);
        Assert.Equal(UtcNow, agentflow.UpdateTime);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_NullCollections_ReturnsEmptyCollections()
    {
        var result = _service.ValidateAndNormalizeGraph(
            null,
            null,
            Guid.NewGuid(),
            []);

        Assert.Empty(result.Nodes!);
        Assert.Empty(result.Edges!);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_EmptyGraph_ReturnsNullCollections()
    {
        var result = _service.ValidateAndNormalizeGraph(
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
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.Agent, RelateId = Guid.NewGuid() },
        };

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            [],
            Guid.NewGuid(),
            []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_DuplicateNodeIds_ReturnsNullCollections()
    {
        var agentId = Guid.NewGuid();
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.Agent, RelateId = agentId },
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
        };

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            [],
            Guid.NewGuid(),
            [agentId]);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_DuplicateEdgeIds_ReturnsNullCollections()
    {
        var agentId = Guid.NewGuid();
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.Agent, RelateId = agentId },
            new AgentflowNode { NodeId = "node-b", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-a", TargetNodeId = "node-b" },
            new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-b", TargetNodeId = "node-a" },
        };

        var result = _service.ValidateAndNormalizeGraph(
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
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.Agent, RelateId = agentId },
        };
        var edges = new[]
        {
            new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-a", TargetNodeId = "node-b" },
        };

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            edges,
            Guid.NewGuid(),
            [agentId]);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_CyclicGraph_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "node-b", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-a", TargetNodeId = "node-b" },
            new AgentflowEdge { EdgeId = "edge-2", SourceNodeId = "node-b", TargetNodeId = "node-a" },
        };

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            edges,
            Guid.NewGuid(),
            []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_MissingInput_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
        };

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            [],
            Guid.NewGuid(),
            []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_IncomingInputEdge_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "edge-1",
                SourceNodeId = "node-a",
                TargetNodeId = "input",
                Kind = AgentflowEdgeKind.Direct,
            },
        };

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            edges,
            Guid.NewGuid(),
            []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_NonFanOutInputEdge_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "edge-1",
                SourceNodeId = "input",
                TargetNodeId = "node-a",
                Kind = AgentflowEdgeKind.Direct,
            },
        };

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            edges,
            Guid.NewGuid(),
            []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_UnreachableVisibleNode_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "node-b", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "edge-1",
                SourceNodeId = "input",
                TargetNodeId = "node-a",
                Kind = AgentflowEdgeKind.FanOut,
            },
        };

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            edges,
            Guid.NewGuid(),
            []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_InvalidConditionJson_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "node-b", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "edge-1",
                SourceNodeId = "node-a",
                TargetNodeId = "node-b",
                Kind = AgentflowEdgeKind.Direct,
                ConditionJson = "{",
            },
        };

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            edges,
            Guid.NewGuid(),
            []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_UnknownConditionProperty_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "node-b", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "edge-1",
                SourceNodeId = "node-a",
                TargetNodeId = "node-b",
                Kind = AgentflowEdgeKind.Direct,
                ConditionJson = """{"script":"return true"}""",
            },
        };

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            edges,
            Guid.NewGuid(),
            []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_ValidDag_ReturnsNormalizedNodesAndEdges()
    {
        var agentflowId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                Name = "Input",
            },
            new AgentflowNode
            {
                NodeId = "node-a",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
                Name = "API Reviewer",
                PositionJson = """{"x":10,"y":20}""",
                Instructions = "Read upstream output carefully.",
                ConfigJson = """{"model":"default"}""",
            },
            new AgentflowNode
            {
                NodeId = "node-b",
                Kind = AgentflowNodeKind.PromptAdapter,
                Name = "Summarize",
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "edge-input",
                SourceNodeId = "input",
                TargetNodeId = "node-a",
                Kind = AgentflowEdgeKind.FanOut,
            },
            new AgentflowEdge
            {
                EdgeId = "edge-a",
                SourceNodeId = "node-a",
                TargetNodeId = "node-b",
                Kind = AgentflowEdgeKind.FanIn,
                Label = "review output",
                ConditionJson = """{"contains":"approved"}""",
                ConfigJson = """{"map":"summary"}""",
            },
        };

        var (normalizedNodes, normalizedEdges) = _service.ValidateAndNormalizeGraph(
            nodes,
            edges,
            agentflowId,
            [agentId]);

        Assert.NotNull(normalizedNodes);
        Assert.NotNull(normalizedEdges);
        Assert.All(normalizedNodes!, node => Assert.Equal(agentflowId, node.AgentflowId));
        Assert.All(normalizedEdges!, edge => Assert.Equal(agentflowId, edge.AgentflowId));
        Assert.Equal(["input", "node-a", "node-b"], normalizedNodes!.Select(node => node.NodeId));
        Assert.Equal(AgentflowNodeKind.Agent, normalizedNodes![1].Kind);
        Assert.Equal("API Reviewer", normalizedNodes![1].Name);
        Assert.Equal("""{"x":10,"y":20}""", normalizedNodes![1].PositionJson);
        Assert.Equal("Read upstream output carefully.", normalizedNodes![1].Instructions);
        Assert.Equal("""{"model":"default"}""", normalizedNodes![1].ConfigJson);
        Assert.Equal(AgentflowEdgeKind.FanIn, normalizedEdges!.Last().Kind);
        Assert.Equal("review output", normalizedEdges!.Last().Label);
        Assert.Equal("""{"contains":"approved"}""", normalizedEdges!.Last().ConditionJson);
        Assert.Equal("""{"map":"summary"}""", normalizedEdges!.Last().ConfigJson);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_SummaryEnabledWithoutModelProvider_ReturnsNullCollections()
    {
        var nodes = CreateSummaryOutputNodes(outputCount: 1);
        var edges = CreateSummaryOutputEdges(outputCount: 1);

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            edges,
            Guid.NewGuid(),
            [],
            summaryModelProviderId: null);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_SummaryEnabledWithMultipleOutputs_ReturnsNullCollections()
    {
        var nodes = CreateSummaryOutputNodes(outputCount: 2);
        var edges = CreateSummaryOutputEdges(outputCount: 2);

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            edges,
            Guid.NewGuid(),
            [],
            summaryModelProviderId: Guid.NewGuid());

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_SummaryEnabledWithSingleOutputAndModelProvider_ReturnsGraph()
    {
        var summaryModelProviderId = Guid.NewGuid();
        var nodes = CreateSummaryOutputNodes(outputCount: 1);
        var edges = CreateSummaryOutputEdges(outputCount: 1);

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            edges,
            Guid.NewGuid(),
            [],
            summaryModelProviderId,
            existingModelProviderIds: [summaryModelProviderId]);

        Assert.NotNull(result.Nodes);
        Assert.NotNull(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_SummaryEnabledWithMissingModelProvider_ReturnsNullCollections()
    {
        var result = _service.ValidateAndNormalizeGraph(
            CreateSummaryOutputNodes(outputCount: 1),
            CreateSummaryOutputEdges(outputCount: 1),
            Guid.NewGuid(),
            [],
            summaryModelProviderId: Guid.NewGuid(),
            existingModelProviderIds: []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void ValidateAndNormalizeGraph_OutputWithInvalidSummaryConfig_ReturnsNullCollections()
    {
        var nodes = CreateSummaryOutputNodes(outputCount: 1).ToArray();
        nodes[1].ConfigJson = """{"enableSummary":"yes"}""";

        var result = _service.ValidateAndNormalizeGraph(
            nodes,
            CreateSummaryOutputEdges(outputCount: 1),
            Guid.NewGuid(),
            [],
            summaryModelProviderId: Guid.NewGuid());

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void OrderNodesByEdges_AcyclicDag_ReturnsTopologicallySortedNodes()
    {
        var first = new AgentflowNode { NodeId = "node-1" };
        var second = new AgentflowNode { NodeId = "node-2" };
        var third = new AgentflowNode { NodeId = "node-3" };

        var result = _service.OrderNodesByEdges(
            [third, second, first],
            [
                new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-1", TargetNodeId = "node-2" },
                new AgentflowEdge { EdgeId = "edge-2", SourceNodeId = "node-2", TargetNodeId = "node-3" },
            ]);

        Assert.Equal(["node-1", "node-2", "node-3"], result.Select(node => node.NodeId));
    }

    private static IReadOnlyList<AgentflowNode> CreateSummaryOutputNodes(int outputCount)
    {
        var nodes = new List<AgentflowNode>
        {
            new() { NodeId = "input", Kind = AgentflowNodeKind.Input }
        };
        nodes.AddRange(Enumerable.Range(1, outputCount).Select(index => new AgentflowNode
        {
            NodeId = $"output-{index}",
            Kind = AgentflowNodeKind.Output,
            ConfigJson = index == 1 ? """{"enableSummary":true}""" : null,
        }));
        return nodes;
    }

    private static IReadOnlyList<AgentflowEdge> CreateSummaryOutputEdges(int outputCount) =>
        Enumerable.Range(1, outputCount)
            .Select(index => new AgentflowEdge
            {
                EdgeId = $"edge-{index}",
                SourceNodeId = "input",
                TargetNodeId = $"output-{index}",
                Kind = AgentflowEdgeKind.FanOut,
            })
            .ToList();
}
