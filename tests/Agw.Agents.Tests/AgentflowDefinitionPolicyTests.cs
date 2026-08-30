using Agw.Agents.Definitions.Domain.Policies;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Tests;

/// <summary>Characterization tests for the pure Agentflow definition policy.</summary>
public class AgentflowDefinitionPolicyTests
{
    private readonly AgentflowDefinitionPolicy _service = new();

    [Fact]
    public void Evaluate_ValidGraph_ReturnsDataOnlyDecision()
    {
        // Arrange
        var agentflowId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
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
        var decision = _service.Evaluate(nodes, edges, agentflowId, []);

        // Assert
        Assert.NotNull(decision.Nodes);
        Assert.NotNull(decision.Edges);
        Assert.All(decision.Nodes, node => Assert.Equal(agentflowId, node.AgentflowId));
        Assert.All(decision.Edges, edge => Assert.Equal(agentflowId, edge.AgentflowId));
    }

    [Fact]
    public void Evaluate_NullCollections_ReturnsEmptyCollections()
    {
        var result = _service.Evaluate(null, null, Guid.CreateVersion7(), []);

        Assert.Empty(result.Nodes!);
        Assert.Empty(result.Edges!);
    }

    [Fact]
    public void Evaluate_EmptyGraph_ReturnsNullCollections()
    {
        var result = _service.Evaluate([], [], Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_MissingRelatedAgent_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "node-a",
                Kind = AgentflowNodeKind.Agent,
                RelateId = Guid.CreateVersion7(),
            },
        };

        var result = _service.Evaluate(nodes, [], Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_DuplicateNodeIds_ReturnsNullCollections()
    {
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "node-a",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            },
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
        };

        var result = _service.Evaluate(nodes, [], Guid.CreateVersion7(), [agentId]);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_DuplicateEdgeIds_ReturnsNullCollections()
    {
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "node-a",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            },
            new AgentflowNode { NodeId = "node-b", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "edge-1",
                SourceNodeId = "node-a",
                TargetNodeId = "node-b",
            },
            new AgentflowEdge
            {
                EdgeId = "edge-1",
                SourceNodeId = "node-b",
                TargetNodeId = "node-a",
            },
        };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), [agentId]);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_EdgeReferencesMissingNode_ReturnsNullCollections()
    {
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "node-a",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "edge-1",
                SourceNodeId = "node-a",
                TargetNodeId = "node-b",
            },
        };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), [agentId]);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_CycleWithoutConditionalExit_ReturnsNullCollections()
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
            },
            new AgentflowEdge
            {
                EdgeId = "edge-2",
                SourceNodeId = "node-a",
                TargetNodeId = "node-b",
            },
            new AgentflowEdge
            {
                EdgeId = "edge-3",
                SourceNodeId = "node-b",
                TargetNodeId = "node-a",
            },
        };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_CycleWithSwitchExitAndReusableInputBarrier_ReturnsGraph()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "upper", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "lower", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "human", Kind = AgentflowNodeKind.HumanGate },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "input-upper",
                SourceNodeId = "input",
                TargetNodeId = "upper",
                Kind = AgentflowEdgeKind.FanOut,
            },
            new AgentflowEdge
            {
                EdgeId = "input-lower",
                SourceNodeId = "input",
                TargetNodeId = "lower",
                Kind = AgentflowEdgeKind.FanInBarrier,
            },
            new AgentflowEdge
            {
                EdgeId = "upper-lower",
                SourceNodeId = "upper",
                TargetNodeId = "lower",
                Kind = AgentflowEdgeKind.FanInBarrier,
            },
            new AgentflowEdge
            {
                EdgeId = "lower-human",
                SourceNodeId = "lower",
                TargetNodeId = "human",
            },
            new AgentflowEdge
            {
                EdgeId = "retry",
                SourceNodeId = "human",
                TargetNodeId = "upper",
                Kind = AgentflowEdgeKind.SwitchCase,
                ConditionJson = """{"contains":"retry"}""",
                ConfigJson = """{"switchCaseOrder":0}""",
            },
            new AgentflowEdge
            {
                EdgeId = "done",
                SourceNodeId = "human",
                TargetNodeId = "output",
                Kind = AgentflowEdgeKind.SwitchDefault,
            },
        };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.NotNull(result.Nodes);
        Assert.NotNull(result.Edges);
        Assert.Equal(nodes.Length, result.Nodes.Count);
        Assert.Equal(edges.Length, result.Edges.Count);
    }

    [Fact]
    public void Evaluate_CyclicBarrierWithExternalNonInputSource_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "seed", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "loop-a", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "loop-b", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "input-seed",
                SourceNodeId = "input",
                TargetNodeId = "seed",
                Kind = AgentflowEdgeKind.FanOut,
            },
            new AgentflowEdge
            {
                EdgeId = "input-loop",
                SourceNodeId = "input",
                TargetNodeId = "loop-a",
                Kind = AgentflowEdgeKind.FanOut,
            },
            new AgentflowEdge
            {
                EdgeId = "seed-barrier",
                SourceNodeId = "seed",
                TargetNodeId = "loop-b",
                Kind = AgentflowEdgeKind.FanInBarrier,
            },
            new AgentflowEdge
            {
                EdgeId = "loop-barrier",
                SourceNodeId = "loop-a",
                TargetNodeId = "loop-b",
                Kind = AgentflowEdgeKind.FanInBarrier,
            },
            new AgentflowEdge
            {
                EdgeId = "retry",
                SourceNodeId = "loop-b",
                TargetNodeId = "loop-a",
                Kind = AgentflowEdgeKind.SwitchCase,
                ConditionJson = """{"contains":"retry"}""",
                ConfigJson = """{"switchCaseOrder":0}""",
            },
            new AgentflowEdge
            {
                EdgeId = "done",
                SourceNodeId = "loop-b",
                TargetNodeId = "output",
                Kind = AgentflowEdgeKind.SwitchDefault,
            },
        };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_MissingInput_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
        };

        var result = _service.Evaluate(nodes, [], Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_IncomingInputEdge_ReturnsNullCollections()
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

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_NonFanOutInputEdge_ReturnsGraph()
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

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.NotNull(result.Nodes);
        Assert.NotNull(result.Edges);
    }

    [Fact]
    public void AgentflowEdgeKind_NumericValues_RemainBackwardCompatible()
    {
        Assert.Equal(0, (int)AgentflowEdgeKind.Direct);
        Assert.Equal(1, (int)AgentflowEdgeKind.FanOut);
        Assert.Equal(2, (int)AgentflowEdgeKind.FanInBarrier);
        Assert.Equal(3, (int)AgentflowEdgeKind.SwitchCase);
        Assert.Equal(4, (int)AgentflowEdgeKind.SwitchDefault);
    }

    [Fact]
    public void Evaluate_ConditionalFanOutEdges_ReturnsGraph()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "always", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "conditional", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "always",
                SourceNodeId = "input",
                TargetNodeId = "always",
                Kind = AgentflowEdgeKind.FanOut,
            },
            new AgentflowEdge
            {
                EdgeId = "conditional",
                SourceNodeId = "input",
                TargetNodeId = "conditional",
                Kind = AgentflowEdgeKind.FanOut,
                ConditionJson = """{"contains":"approved"}""",
            },
        };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.NotNull(result.Nodes);
        Assert.NotNull(result.Edges);
    }

    [Fact]
    public void Evaluate_UnreachableVisibleNode_ReturnsNullCollections()
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

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_InvalidConditionJson_ReturnsNullCollections()
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

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_UnknownConditionProperty_ReturnsNullCollections()
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

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_MixedSourceRoutingStrategies_ReturnsNullCollections()
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
                EdgeId = "direct",
                SourceNodeId = "input",
                TargetNodeId = "node-a",
                Kind = AgentflowEdgeKind.Direct,
            },
            new AgentflowEdge
            {
                EdgeId = "fan-out",
                SourceNodeId = "input",
                TargetNodeId = "node-b",
                Kind = AgentflowEdgeKind.FanOut,
            },
        };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_OrderedSwitchWithDefault_ReturnsGraph()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "first", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "second", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "fallback", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            SwitchCase("case-1", "input", "first", 0, "approved"),
            SwitchCase("case-2", "input", "second", 1, "review"),
            new AgentflowEdge
            {
                EdgeId = "default",
                SourceNodeId = "input",
                TargetNodeId = "fallback",
                Kind = AgentflowEdgeKind.SwitchDefault,
            },
        };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.NotNull(result.Nodes);
        Assert.NotNull(result.Edges);
    }

    [Fact]
    public void Evaluate_SwitchWithoutDefault_ReturnsGraph()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "approved", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[] { SwitchCase("approved", "input", "approved", 0, "approved") };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.NotNull(result.Nodes);
        Assert.NotNull(result.Edges);
    }

    [Fact]
    public void Evaluate_DuplicateSwitchOrder_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "first", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "second", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            SwitchCase("case-1", "input", "first", 0, "approved"),
            SwitchCase("case-2", "input", "second", 0, "review"),
        };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_MultipleSwitchDefaults_ReturnsNullCollections()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "first", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "fallback-a", Kind = AgentflowNodeKind.PromptAdapter },
            new AgentflowNode { NodeId = "fallback-b", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[]
        {
            SwitchCase("case-1", "input", "first", 0, "approved"),
            new AgentflowEdge
            {
                EdgeId = "default-a",
                SourceNodeId = "input",
                TargetNodeId = "fallback-a",
                Kind = AgentflowEdgeKind.SwitchDefault,
            },
            new AgentflowEdge
            {
                EdgeId = "default-b",
                SourceNodeId = "input",
                TargetNodeId = "fallback-b",
                Kind = AgentflowEdgeKind.SwitchDefault,
            },
        };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_InputFanOutAndBarrierEdges_ReturnsGraph()
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
                EdgeId = "input-a",
                SourceNodeId = "input",
                TargetNodeId = "node-a",
                Kind = AgentflowEdgeKind.FanOut,
            },
            new AgentflowEdge
            {
                EdgeId = "input-b",
                SourceNodeId = "input",
                TargetNodeId = "node-b",
                Kind = AgentflowEdgeKind.FanInBarrier,
            },
            new AgentflowEdge
            {
                EdgeId = "a-b",
                SourceNodeId = "node-a",
                TargetNodeId = "node-b",
                Kind = AgentflowEdgeKind.FanInBarrier,
            },
        };

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), []);

        Assert.NotNull(result.Nodes);
        Assert.NotNull(result.Edges);
    }

    [Fact]
    public void Evaluate_ValidDag_ReturnsNormalizedNodesAndEdges()
    {
        var agentflowId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
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
                Kind = AgentflowEdgeKind.FanInBarrier,
                Label = "review output",
                ConditionJson = """{"contains":"approved"}""",
                ConfigJson = """{"map":"summary"}""",
            },
        };

        var decision = _service.Evaluate(
            nodes,
            edges,
            agentflowId,
            [agentId],
            existingAgentNames: new Dictionary<Guid, string> { [agentId] = "default-agent-name" }
        );
        var normalizedNodes = decision.Nodes;
        var normalizedEdges = decision.Edges;

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
        Assert.Equal(AgentflowEdgeKind.FanInBarrier, normalizedEdges!.Last().Kind);
        Assert.Equal("review output", normalizedEdges!.Last().Label);
        Assert.Equal("""{"contains":"approved"}""", normalizedEdges!.Last().ConditionJson);
        Assert.Equal("""{"map":"summary"}""", normalizedEdges!.Last().ConfigJson);
    }

    [Fact]
    public void Evaluate_AgentNodeWithoutName_DefaultsToAgentName()
    {
        var agentflowId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode
            {
                NodeId = "agent",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
                Name = " ",
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "input-agent",
                SourceNodeId = "input",
                TargetNodeId = "agent",
            },
        };

        var decision = _service.Evaluate(
            nodes,
            edges,
            agentflowId,
            [agentId],
            existingAgentNames: new Dictionary<Guid, string> { [agentId] = "default-agent-name" }
        );
        var normalizedNodes = decision.Nodes;
        var normalizedEdges = decision.Edges;

        Assert.NotNull(normalizedNodes);
        Assert.NotNull(normalizedEdges);
        Assert.Equal("default-agent-name", normalizedNodes![1].Name);
    }

    [Fact]
    public void Evaluate_SummaryEnabledWithoutModelProvider_ReturnsNullCollections()
    {
        var nodes = CreateSummaryOutputNodes(outputCount: 1);
        var edges = CreateSummaryOutputEdges(outputCount: 1);

        var result = _service.Evaluate(nodes, edges, Guid.CreateVersion7(), [], summaryModelProviderId: null);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_SummaryEnabledWithMultipleOutputs_ReturnsNullCollections()
    {
        var nodes = CreateSummaryOutputNodes(outputCount: 2);
        var edges = CreateSummaryOutputEdges(outputCount: 2);

        var result = _service.Evaluate(
            nodes,
            edges,
            Guid.CreateVersion7(),
            [],
            summaryModelProviderId: Guid.CreateVersion7()
        );

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_SummaryEnabledWithSingleOutputAndModelProvider_ReturnsGraph()
    {
        var summaryModelProviderId = Guid.CreateVersion7();
        var nodes = CreateSummaryOutputNodes(outputCount: 1);
        var edges = CreateSummaryOutputEdges(outputCount: 1);

        var result = _service.Evaluate(
            nodes,
            edges,
            Guid.CreateVersion7(),
            [],
            summaryModelProviderId,
            existingModelProviderIds: [summaryModelProviderId]
        );

        Assert.NotNull(result.Nodes);
        Assert.NotNull(result.Edges);
    }

    [Fact]
    public void Evaluate_SummaryEnabledWithMissingModelProvider_ReturnsNullCollections()
    {
        var result = _service.Evaluate(
            CreateSummaryOutputNodes(outputCount: 1),
            CreateSummaryOutputEdges(outputCount: 1),
            Guid.CreateVersion7(),
            [],
            summaryModelProviderId: Guid.CreateVersion7(),
            existingModelProviderIds: []
        );

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void Evaluate_OutputWithInvalidSummaryConfig_ReturnsNullCollections()
    {
        var nodes = CreateSummaryOutputNodes(outputCount: 1).ToArray();
        nodes[1].ConfigJson = """{"enableSummary":"yes"}""";

        var result = _service.Evaluate(
            nodes,
            CreateSummaryOutputEdges(outputCount: 1),
            Guid.CreateVersion7(),
            [],
            summaryModelProviderId: Guid.CreateVersion7()
        );

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    private static IReadOnlyList<AgentflowNode> CreateSummaryOutputNodes(int outputCount)
    {
        var nodes = new List<AgentflowNode>
        {
            new() { NodeId = "input", Kind = AgentflowNodeKind.Input },
        };
        nodes.AddRange(
            Enumerable
                .Range(1, outputCount)
                .Select(index => new AgentflowNode
                {
                    NodeId = $"output-{index}",
                    Kind = AgentflowNodeKind.Output,
                    ConfigJson = index == 1 ? """{"enableSummary":true}""" : null,
                })
        );
        return nodes;
    }

    private static AgentflowEdge SwitchCase(
        string edgeId,
        string sourceNodeId,
        string targetNodeId,
        int order,
        string contains
    )
    {
        return new AgentflowEdge
        {
            EdgeId = edgeId,
            SourceNodeId = sourceNodeId,
            TargetNodeId = targetNodeId,
            Kind = AgentflowEdgeKind.SwitchCase,
            ConditionJson = $$"""{"contains":"{{contains}}"}""",
            ConfigJson = $$"""{"switchCaseOrder":{{order}}}""",
        };
    }

    private static IReadOnlyList<AgentflowEdge> CreateSummaryOutputEdges(int outputCount) =>
        Enumerable
            .Range(1, outputCount)
            .Select(index => new AgentflowEdge
            {
                EdgeId = $"edge-{index}",
                SourceNodeId = "input",
                TargetNodeId = $"output-{index}",
                Kind = AgentflowEdgeKind.FanOut,
            })
            .ToList();
}
