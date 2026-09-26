using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Tests;

/// <summary>Characterization tests for the Agentflow graph rules owned by AgentflowBehavior.</summary>
public class AgentflowBehaviorGraphTests
{
    [Fact]
    public void TryReplaceGraph_ValidGraph_StampsAgentflowIdOnNodesAndEdges()
    {
        // Arrange
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
        var result = Replace(nodes, edges);

        // Assert
        Assert.True(result.Replaced);
        Assert.All(result.Agentflow.Nodes, node => Assert.Equal(result.Agentflow.Id, node.AgentflowId));
        Assert.All(result.Agentflow.Edges, edge => Assert.Equal(result.Agentflow.Id, edge.AgentflowId));
    }

    [Fact]
    public void TryReplaceGraph_EmptyGraph_ReturnsFalse()
    {
        var result = Replace([], []);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_DuplicateNodeIds_ReturnsFalse()
    {
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "node-a",
                Kind = AgentflowNodeKind.Agent,
                RelateId = Guid.CreateVersion7(),
            },
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
        };

        var result = Replace(nodes, []);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_DuplicateEdgeIds_ReturnsFalse()
    {
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "node-a",
                Kind = AgentflowNodeKind.Agent,
                RelateId = Guid.CreateVersion7(),
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

        var result = Replace(nodes, edges);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_EdgeReferencesMissingNode_ReturnsFalse()
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
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "edge-1",
                SourceNodeId = "node-a",
                TargetNodeId = "node-b",
            },
        };

        var result = Replace(nodes, edges);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_CycleWithoutConditionalExit_ReturnsFalse()
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

        var result = Replace(nodes, edges);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_CycleWithSwitchExitAndReusableInputBarrier_ReplacesGraph()
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

        var result = Replace(nodes, edges);

        Assert.True(result.Replaced);
        Assert.Equal(nodes.Length, result.Agentflow.Nodes.Count);
        Assert.Equal(edges.Length, result.Agentflow.Edges.Count);
    }

    [Fact]
    public void TryReplaceGraph_CyclicBarrierWithExternalNonInputSource_ReturnsFalse()
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

        var result = Replace(nodes, edges);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_MissingInput_ReturnsFalse()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "node-a", Kind = AgentflowNodeKind.PromptAdapter },
        };

        var result = Replace(nodes, []);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_IncomingInputEdge_ReturnsFalse()
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

        var result = Replace(nodes, edges);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_NonFanOutInputEdge_ReplacesGraph()
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

        var result = Replace(nodes, edges);

        Assert.True(result.Replaced);
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
    public void TryReplaceGraph_ConditionalFanOutEdges_ReplacesGraph()
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

        var result = Replace(nodes, edges);

        Assert.True(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_UnreachableVisibleNode_ReturnsFalse()
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

        var result = Replace(nodes, edges);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_InvalidConditionJson_ReturnsFalse()
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

        var result = Replace(nodes, edges);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_UnknownConditionProperty_ReturnsFalse()
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

        var result = Replace(nodes, edges);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_MixedSourceRoutingStrategies_ReturnsFalse()
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

        var result = Replace(nodes, edges);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_OrderedSwitchWithDefault_ReplacesGraph()
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

        var result = Replace(nodes, edges);

        Assert.True(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_SwitchWithoutDefault_ReplacesGraph()
    {
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "approved", Kind = AgentflowNodeKind.PromptAdapter },
        };
        var edges = new[] { SwitchCase("approved", "input", "approved", 0, "approved") };

        var result = Replace(nodes, edges);

        Assert.True(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_DuplicateSwitchOrder_ReturnsFalse()
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

        var result = Replace(nodes, edges);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_MultipleSwitchDefaults_ReturnsFalse()
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

        var result = Replace(nodes, edges);

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_InputFanOutAndBarrierEdges_ReplacesGraph()
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

        var result = Replace(nodes, edges);

        Assert.True(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_ValidDag_StoresNormalizedNodesAndEdges()
    {
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

        var result = Replace(
            nodes,
            edges,
            agentNames: new Dictionary<Guid, string> { [agentId] = "default-agent-name" }
        );
        var normalizedNodes = result.Agentflow.Nodes.ToList();
        var normalizedEdges = result.Agentflow.Edges.ToList();

        Assert.True(result.Replaced);
        Assert.All(normalizedNodes, node => Assert.Equal(result.Agentflow.Id, node.AgentflowId));
        Assert.All(normalizedEdges, edge => Assert.Equal(result.Agentflow.Id, edge.AgentflowId));
        Assert.Equal(["input", "node-a", "node-b"], normalizedNodes.Select(node => node.NodeId));
        Assert.Equal(AgentflowNodeKind.Agent, normalizedNodes[1].Kind);
        Assert.Equal("API Reviewer", normalizedNodes[1].Name);
        Assert.Equal("""{"x":10,"y":20}""", normalizedNodes[1].PositionJson);
        Assert.Equal("Read upstream output carefully.", normalizedNodes[1].Instructions);
        Assert.Equal("""{"model":"default"}""", normalizedNodes[1].ConfigJson);
        Assert.Equal(AgentflowEdgeKind.FanInBarrier, normalizedEdges.Last().Kind);
        Assert.Equal("review output", normalizedEdges.Last().Label);
        Assert.Equal("""{"contains":"approved"}""", normalizedEdges.Last().ConditionJson);
        Assert.Equal("""{"map":"summary"}""", normalizedEdges.Last().ConfigJson);
    }

    [Fact]
    public void TryReplaceGraph_AgentNodeWithoutName_DefaultsToAgentName()
    {
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

        var result = Replace(
            nodes,
            edges,
            agentNames: new Dictionary<Guid, string> { [agentId] = "default-agent-name" }
        );

        Assert.True(result.Replaced);
        Assert.Equal("default-agent-name", result.Agentflow.Nodes.Single(node => node.NodeId == "agent").Name);
    }

    [Fact]
    public void TryReplaceGraph_SummaryEnabledWithoutModelProvider_ReturnsFalse()
    {
        var result = Replace(CreateSummaryOutputNodes(outputCount: 1), CreateSummaryOutputEdges(outputCount: 1));

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_SummaryEnabledWithMultipleOutputs_ReturnsFalse()
    {
        var result = Replace(
            CreateSummaryOutputNodes(outputCount: 2),
            CreateSummaryOutputEdges(outputCount: 2),
            summaryModelProviderId: Guid.CreateVersion7(),
            summaryModelProviderVisible: true
        );

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_SummaryEnabledWithSingleOutputAndVisibleModelProvider_ReplacesGraph()
    {
        var result = Replace(
            CreateSummaryOutputNodes(outputCount: 1),
            CreateSummaryOutputEdges(outputCount: 1),
            summaryModelProviderId: Guid.CreateVersion7(),
            summaryModelProviderVisible: true
        );

        Assert.True(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_SummaryEnabledWithInvisibleModelProvider_ReturnsFalse()
    {
        var result = Replace(
            CreateSummaryOutputNodes(outputCount: 1),
            CreateSummaryOutputEdges(outputCount: 1),
            summaryModelProviderId: Guid.CreateVersion7(),
            summaryModelProviderVisible: false
        );

        Assert.False(result.Replaced);
    }

    [Fact]
    public void TryReplaceGraph_OutputWithInvalidSummaryConfig_ReturnsFalse()
    {
        var nodes = CreateSummaryOutputNodes(outputCount: 1).ToArray();
        nodes[1].ConfigJson = """{"enableSummary":"yes"}""";

        var result = Replace(
            nodes,
            CreateSummaryOutputEdges(outputCount: 1),
            summaryModelProviderId: Guid.CreateVersion7(),
            summaryModelProviderVisible: true
        );

        Assert.False(result.Replaced);
    }

    private static GraphResult Replace(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        Guid? summaryModelProviderId = null,
        bool summaryModelProviderVisible = false,
        IReadOnlyDictionary<Guid, string>? agentNames = null
    )
    {
        var agentflow = new Agentflow { Id = Guid.CreateVersion7(), SummaryModelProviderId = summaryModelProviderId };
        var replaced = new AgentflowBehavior(agentflow).TryReplaceGraph(
            nodes,
            edges,
            AgentflowConfigurationParser.Parse(nodes, edges),
            agentNames ?? new Dictionary<Guid, string>(),
            summaryModelProviderVisible
        );
        return new GraphResult(replaced, agentflow);
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

    private sealed record GraphResult(bool Replaced, Agentflow Agentflow);
}
