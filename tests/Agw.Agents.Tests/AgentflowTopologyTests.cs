using Agw.Agents.Definitions.Domain.Topology;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Tests;

public sealed class AgentflowTopologyTests
{
    [Fact]
    public void OrderNodesByEdges_AcyclicDag_ReturnsTopologicallySortedNodes()
    {
        // Arrange
        var first = new AgentflowNode { NodeId = "node-1" };
        var second = new AgentflowNode { NodeId = "node-2" };
        var third = new AgentflowNode { NodeId = "node-3" };
        // Act
        var result = AgentflowTopology.OrderNodesByEdges(
            [third, second, first],
            [
                new AgentflowEdge
                {
                    EdgeId = "edge-1",
                    SourceNodeId = "node-1",
                    TargetNodeId = "node-2",
                },
                new AgentflowEdge
                {
                    EdgeId = "edge-2",
                    SourceNodeId = "node-2",
                    TargetNodeId = "node-3",
                },
            ]
        );

        // Assert
        Assert.Equal(["node-1", "node-2", "node-3"], result.Select(node => node.NodeId));
    }
}
