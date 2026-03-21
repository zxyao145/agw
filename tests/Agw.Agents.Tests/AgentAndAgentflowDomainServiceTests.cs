using Agw.Domain.Entities;
using Agw.Domain.Services.Agents;
using Agw.Domain.Services.Agentflows;
using Agw.Shared.Enums;

namespace Agw.Agents.Tests;

public class AgentAndAgentflowDomainServiceTests
{
    private readonly AgentDomainService _agentDomainService = new();
    private readonly AgentflowDomainService _agentflowDomainService = new();
    private readonly McpToolServerDomainService _mcpToolServerDomainService = new();

    [Fact]
    public void PrepareForCreate_SystemAgentWithoutModelProvider_ThrowsInvalidOperationException()
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            ModelProviderId = null,
        };

        Assert.Throws<InvalidOperationException>(() => _agentDomainService.PrepareForCreate(agent, "tester"));
    }

    [Fact]
    public void ApplyUpdate_ExternalAgent_PreservesImmutableFieldsWhileUpdatingMetadata()
    {
        var originalId = Guid.NewGuid();
        var originalCreateTime = DateTime.UtcNow.AddDays(-1);
        var agent = new Agent
        {
            Id = originalId,
            Name = "original-name",
            SystemPrompt = "original-prompt",
            Tools = "[\"tool-a\"]",
            Type = AgentType.External,
            DisplayName = "Before",
            CreateBy = "creator",
            CreateTime = originalCreateTime,
        };
        var updatedModelProviderId = Guid.NewGuid();

        _agentDomainService.ApplyUpdate(
            agent,
            current =>
            {
                current.Id = Guid.NewGuid();
                current.Name = "updated-name";
                current.SystemPrompt = "updated-prompt";
                current.Tools = "[\"tool-b\"]";
                current.Type = AgentType.System;
                current.DisplayName = "After";
                current.ModelProviderId = updatedModelProviderId;
            },
            "updater");

        Assert.Equal(originalId, agent.Id);
        Assert.Equal("original-name", agent.Name);
        Assert.Equal("original-prompt", agent.SystemPrompt);
        Assert.Equal("[\"tool-a\"]", agent.Tools);
        Assert.Equal(AgentType.External, agent.Type);
        Assert.Equal("After", agent.DisplayName);
        Assert.Equal(updatedModelProviderId, agent.ModelProviderId);
        Assert.Equal("updater", agent.UpdateBy);
        Assert.True(agent.UpdateTime >= originalCreateTime);
    }

    [Fact]
    public void NormalizeMcpToolServerIds_RemovesEmptyValuesAndDuplicates()
    {
        var id = Guid.NewGuid();

        var result = _agentDomainService.NormalizeMcpToolServerIds([Guid.Empty, id, id]);

        Assert.Equal([id], result);
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

        var (normalizedNodes, normalizedEdges) = _agentflowDomainService.ValidateAndNormalizeGraph(
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
    public void ValidateAndNormalizeGraph_DuplicateNodeIds_ReturnsNullCollections()
    {
        var duplicateNodeId = "duplicate-node";
        var nodes = new[]
        {
            new AgentflowNode { NodeId = duplicateNodeId, Type = AgentflowNodeType.AgentflowNode },
            new AgentflowNode { NodeId = duplicateNodeId, Type = AgentflowNodeType.AgentflowNode },
        };

        var result = _agentflowDomainService.ValidateAndNormalizeGraph(
            AgentflowOrchestrationPattern.Sequential,
            nodes,
            [],
            Guid.NewGuid(),
            []);

        Assert.Null(result.Nodes);
        Assert.Null(result.Edges);
    }

    [Fact]
    public void OrderNodesByEdges_AcyclicSequentialGraph_ReturnsTopologicallySortedNodes()
    {
        var first = new AgentflowNode { NodeId = "node-1" };
        var second = new AgentflowNode { NodeId = "node-2" };
        var third = new AgentflowNode { NodeId = "node-3" };

        var result = _agentflowDomainService.OrderNodesByEdges(
            [third, second, first],
            [
                new AgentflowEdge { EdgeId = "edge-1", SourceNodeId = "node-1", TargetNodeId = "node-2" },
                new AgentflowEdge { EdgeId = "edge-2", SourceNodeId = "node-2", TargetNodeId = "node-3" },
            ],
            AgentflowOrchestrationPattern.Sequential);

        Assert.Equal(["node-1", "node-2", "node-3"], result.Select(node => node.NodeId));
    }

    [Fact]
    public void PrepareForCreate_McpToolServer_InitializesOptionalCollections()
    {
        var server = new McpToolServer
        {
            Name = "stdio-server",
            Arguments = null!,
            EnvironmentVariables = null!,
            Headers = null!,
        };

        _mcpToolServerDomainService.PrepareForCreate(server, "tester");

        Assert.NotEqual(Guid.Empty, server.Id);
        Assert.Empty(server.Arguments);
        Assert.Empty(server.EnvironmentVariables);
        Assert.Empty(server.Headers);
        Assert.Equal("tester", server.CreateBy);
    }
}
