using Agw.Domain.Entities;
using Agw.Shared.Enums;

namespace Agw.Domain.Services.Agentflows;

public class AgentflowDomainService
{
    public bool TryPrepareForCreate(Agentflow agentflow, string user)
    {
        ArgumentNullException.ThrowIfNull(agentflow);
        if (string.IsNullOrWhiteSpace(agentflow.Name))
        {
            return false;
        }

        agentflow.Id = agentflow.Id == Guid.Empty ? Guid.NewGuid() : agentflow.Id;
        agentflow.CreateBy = user;
        agentflow.CreateTime = DateTime.UtcNow;
        return true;
    }

    public bool TryApplyUpdate(Agentflow existing, Action<Agentflow> updateAction, string user)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(updateAction);

        updateAction(existing);
        if (string.IsNullOrWhiteSpace(existing.Name))
        {
            return false;
        }

        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;
        return true;
    }

    public (IReadOnlyList<AgentflowNode>? Nodes, IReadOnlyList<AgentflowEdge>? Edges) ValidateAndNormalizeGraph(
        AgentflowOrchestrationPattern pattern,
        IReadOnlyList<AgentflowNode>? nodes,
        IReadOnlyList<AgentflowEdge>? edges,
        Guid agentflowId,
        IReadOnlyCollection<Guid> existingAgentIds)
    {
        if (nodes == null || edges == null)
        {
            return (Array.Empty<AgentflowNode>(), Array.Empty<AgentflowEdge>());
        }

        if (pattern == AgentflowOrchestrationPattern.Sequential && nodes.Count == 0)
        {
            return (null, null);
        }

        var nodeIds = nodes.Select(x => x.NodeId).ToList();
        if (nodeIds.Count == 0)
        {
            return (Array.Empty<AgentflowNode>(), Array.Empty<AgentflowEdge>());
        }

        if (nodeIds.Distinct(StringComparer.Ordinal).Count() != nodeIds.Count)
        {
            return (null, null);
        }

        var agentIdSet = existingAgentIds.ToHashSet();
        var relatedAgentIds = nodes
            .Where(x => x.Type == AgentflowNodeType.AgentNode)
            .Select(x => x.RelateId)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
        if (relatedAgentIds.Any(id => !agentIdSet.Contains(id)))
        {
            return (null, null);
        }

        var edgeIds = edges.Select(x => x.EdgeId).ToList();
        if (edgeIds.Distinct(StringComparer.Ordinal).Count() != edgeIds.Count)
        {
            return (null, null);
        }

        foreach (var edge in edges)
        {
            if (!nodeIds.Contains(edge.SourceNodeId, StringComparer.Ordinal) ||
                !nodeIds.Contains(edge.TargetNodeId, StringComparer.Ordinal))
            {
                return (null, null);
            }
        }

        var normalizedNodes = nodes
            .Select(x => new AgentflowNode
            {
                AgentflowId = agentflowId,
                NodeId = x.NodeId,
                Type = x.Type,
                RelateId = x.RelateId,
            })
            .ToList();

        var normalizedEdges = edges
            .Select(x => new AgentflowEdge
            {
                AgentflowId = agentflowId,
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Animated = x.Animated,
            })
            .ToList();

        return (normalizedNodes, normalizedEdges);
    }

    public IReadOnlyList<AgentflowNode> OrderNodesByEdges(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        AgentflowOrchestrationPattern pattern)
    {
        if (pattern == AgentflowOrchestrationPattern.Concurrent ||
            pattern == AgentflowOrchestrationPattern.GroupChat)
        {
            return nodes;
        }

        if (edges.Count == 0)
        {
            return nodes;
        }

        var nodeMap = nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        var adjList = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            adjList[node.NodeId] = [];
            inDegree[node.NodeId] = 0;
        }

        foreach (var edge in edges)
        {
            if (adjList.ContainsKey(edge.SourceNodeId) && inDegree.ContainsKey(edge.TargetNodeId))
            {
                adjList[edge.SourceNodeId].Add(edge.TargetNodeId);
                inDegree[edge.TargetNodeId]++;
            }
        }

        var queue = new Queue<string>(nodes.Where(node => inDegree[node.NodeId] == 0).Select(node => node.NodeId));
        var sorted = new List<AgentflowNode>(nodes.Count);

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            sorted.Add(nodeMap[nodeId]);

            foreach (var neighbor in adjList[nodeId])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        return sorted.Count == nodes.Count ? sorted : nodes;
    }
}
