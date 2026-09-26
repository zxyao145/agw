using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Tests;

public sealed class AgentflowBehaviorTests
{
    [Fact]
    public void HasValidName_BlankName_ReturnsFalse()
    {
        // Arrange
        var behavior = new AgentflowBehavior(new Agentflow { Name = "  " });

        // Act
        var result = behavior.HasValidName();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void TryReplaceGraph_ValidGraph_ReconcilesOwnedChildrenByKey()
    {
        // Arrange
        var agentflow = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "review-flow",
            Nodes =
            [
                new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
                new AgentflowNode { NodeId = "obsolete", Kind = AgentflowNodeKind.Output },
            ],
            Edges = [new AgentflowEdge { EdgeId = "obsolete-edge" }],
        };
        var trackedInput = agentflow.Nodes.First();
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "input",
                Kind = AgentflowNodeKind.Input,
                Name = "Start",
            },
            new AgentflowNode
            {
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
                Name = "Result",
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
        var result = new AgentflowBehavior(agentflow).TryReplaceGraph(
            nodes,
            edges,
            AgentflowConfigurationParser.Parse(nodes, edges),
            new Dictionary<Guid, string>(),
            summaryModelProviderVisible: false
        );

        // Assert
        Assert.True(result);
        Assert.Same(trackedInput, agentflow.Nodes.Single(node => node.NodeId == "input"));
        Assert.Equal("Start", trackedInput.Name);
        Assert.Equal(agentflow.Id, agentflow.Nodes.Single(node => node.NodeId == "output").AgentflowId);
        Assert.DoesNotContain(agentflow.Nodes, node => node.NodeId == "obsolete");
        Assert.Equal("input-output", Assert.Single(agentflow.Edges).EdgeId);
        Assert.Equal(agentflow.Id, agentflow.Edges.Single().AgentflowId);
    }

    [Fact]
    public void TryReplaceGraph_InvalidGraph_ReturnsFalseWithoutMutation()
    {
        // Arrange
        var agentflow = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "review-flow",
            Nodes = [new AgentflowNode { NodeId = "reviewer" }],
            Edges = [],
        };
        var originalNode = agentflow.Nodes.Single();
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };

        // Act
        var result = new AgentflowBehavior(agentflow).TryReplaceGraph(
            nodes,
            [],
            AgentflowConfigurationParser.Parse(nodes, []),
            new Dictionary<Guid, string>(),
            summaryModelProviderVisible: false
        );

        // Assert
        Assert.False(result);
        Assert.Same(originalNode, Assert.Single(agentflow.Nodes));
    }
}
