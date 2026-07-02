using System.Text.Json;

using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Domain.Services;

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
        IReadOnlyList<AgentflowNode>? nodes,
        IReadOnlyList<AgentflowEdge>? edges,
        Guid agentflowId,
        IReadOnlyCollection<Guid> existingAgentIds)
    {
        if (nodes == null || edges == null)
        {
            return (Array.Empty<AgentflowNode>(), Array.Empty<AgentflowEdge>());
        }

        if (nodes.Count == 0)
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
            .Where(x => x.Kind == AgentflowNodeKind.Agent)
            .Select(x => x.RelateId)
            .Where(x => x is not null && x.Value != Guid.Empty)
            .Select(x => x!.Value)
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

            if (!IsValidConditionJson(edge.ConditionJson))
            {
                return (null, null);
            }
        }

        if (HasCycle(nodeIds, edges))
        {
            return (null, null);
        }

        var normalizedNodes = nodes
            .Select(x => new AgentflowNode
            {
                AgentflowId = agentflowId,
                NodeId = x.NodeId,
                Kind = x.Kind,
                RelateId = x.RelateId,
                Name = x.Name,
                PositionJson = x.PositionJson,
                Instructions = x.Instructions,
                ConfigJson = x.ConfigJson,
            })
            .ToList();

        var normalizedEdges = edges
            .Select(x => new AgentflowEdge
            {
                AgentflowId = agentflowId,
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Kind = x.Kind,
                Label = x.Label,
                ConditionJson = x.ConditionJson,
                ConfigJson = x.ConfigJson,
            })
            .ToList();

        return (normalizedNodes, normalizedEdges);
    }

    public IReadOnlyList<AgentflowNode> OrderNodesByEdges(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges)
    {
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

    private static bool IsValidConditionJson(string? conditionJson)
    {
        if (string.IsNullOrWhiteSpace(conditionJson))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(conditionJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var hasKnownCondition = false;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                hasKnownCondition = true;
                if (!IsValidConditionProperty(property))
                {
                    return false;
                }
            }

            return hasKnownCondition;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidConditionProperty(JsonProperty property)
    {
        return property.Name switch
        {
            "always" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "contains" or "notContains" or "equals" or "author" or "role" =>
                property.Value.ValueKind == JsonValueKind.String,
            "minMessages" => property.Value.ValueKind == JsonValueKind.Number,
            _ => false,
        };
    }

    private static bool HasCycle(IReadOnlyList<string> nodeIds, IReadOnlyList<AgentflowEdge> edges)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var adjacency = nodeIds.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            adjacency[edge.SourceNodeId].Add(edge.TargetNodeId);
        }

        bool Visit(string nodeId)
        {
            if (visiting.Contains(nodeId))
            {
                return true;
            }

            if (visited.Contains(nodeId))
            {
                return false;
            }

            visiting.Add(nodeId);
            foreach (var next in adjacency[nodeId])
            {
                if (Visit(next))
                {
                    return true;
                }
            }

            visiting.Remove(nodeId);
            visited.Add(nodeId);
            return false;
        }

        return nodeIds.Any(Visit);
    }
}
