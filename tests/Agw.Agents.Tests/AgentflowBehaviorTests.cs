using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Decisions;
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
    public void TryApplyGraphDecision_ValidDecision_MutatesOwnedChildren()
    {
        // Arrange
        var agentflow = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "review-flow",
            Nodes = [],
            Edges = [],
        };
        var decision = new AgentflowDefinitionDecision
        {
            Nodes =
            [
                new AgentflowNode
                {
                    AgentflowId = agentflow.Id,
                    NodeId = "input",
                    Kind = AgentflowNodeKind.Input,
                },
                new AgentflowNode
                {
                    AgentflowId = agentflow.Id,
                    NodeId = "reviewer",
                    Name = "Reviewer",
                },
            ],
            Edges = [new AgentflowEdge { AgentflowId = agentflow.Id, EdgeId = "input-reviewer" }],
        };
        var behavior = new AgentflowBehavior(agentflow);

        // Act
        var result = behavior.TryApplyGraphDecision(decision);

        // Assert
        Assert.True(result);
        Assert.Equal("Reviewer", agentflow.Nodes.Single(node => node.NodeId == "reviewer").Name);
        Assert.Equal(agentflow.Id, agentflow.Nodes.Single(node => node.NodeId == "reviewer").AgentflowId);
        Assert.Equal(agentflow.Id, agentflow.Edges.Single().AgentflowId);
    }

    [Fact]
    public void TryApplyGraphDecision_InvalidDecision_ReturnsFalseWithoutMutation()
    {
        // Arrange
        var agentflow = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "review-flow",
            Nodes = [new AgentflowNode { NodeId = "reviewer" }],
            Edges = [],
        };
        var originalNodes = agentflow.Nodes;
        var behavior = new AgentflowBehavior(agentflow);
        var decision = new AgentflowDefinitionDecision();

        // Act
        var result = behavior.TryApplyGraphDecision(decision);

        // Assert
        Assert.False(result);
        Assert.Same(originalNodes, agentflow.Nodes);
    }
}
