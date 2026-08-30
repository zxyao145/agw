using System.Text.Json;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Domain.Topology;

public static class AgentflowTopology
{
    public static IReadOnlyList<AgentflowNode> OrderNodesByEdges(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges
    )
    {
        if (edges.Count == 0)
        {
            return nodes;
        }

        var nodeMap = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            adjacency[node.NodeId] = [];
            inDegree[node.NodeId] = 0;
        }

        foreach (var edge in edges)
        {
            if (adjacency.ContainsKey(edge.SourceNodeId) && inDegree.ContainsKey(edge.TargetNodeId))
            {
                adjacency[edge.SourceNodeId].Add(edge.TargetNodeId);
                inDegree[edge.TargetNodeId]++;
            }
        }

        var queue = new Queue<string>(nodes.Where(node => inDegree[node.NodeId] == 0).Select(node => node.NodeId));
        var sorted = new List<AgentflowNode>(nodes.Count);

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            sorted.Add(nodeMap[nodeId]);

            foreach (var neighbor in adjacency[nodeId])
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

    public static bool TryReadSwitchCaseOrder(string? configJson, out int order)
    {
        order = 0;
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("switchCaseOrder", out var property)
                || property.ValueKind != JsonValueKind.Number
                || !property.TryGetInt32(out order)
                || order < 0
            )
            {
                order = 0;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryReadOutputSummaryEnabled(string? configJson, out bool enabled)
    {
        enabled = false;
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("enableSummary", out var property))
            {
                return true;
            }

            if (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                return false;
            }

            enabled = property.GetBoolean();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IReadOnlyList<HashSet<string>> FindCyclicComponents(
        IReadOnlyCollection<string> nodeIds,
        IReadOnlyList<AgentflowEdge> edges
    )
    {
        var adjacency = nodeIds.ToDictionary(nodeId => nodeId, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            adjacency[edge.SourceNodeId].Add(edge.TargetNodeId);
        }

        var nextIndex = 0;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var cyclicComponents = new List<HashSet<string>>();

        void Visit(string nodeId)
        {
            indexes[nodeId] = nextIndex;
            lowLinks[nodeId] = nextIndex;
            nextIndex++;
            stack.Push(nodeId);
            onStack.Add(nodeId);

            foreach (var next in adjacency[nodeId])
            {
                if (!indexes.ContainsKey(next))
                {
                    Visit(next);
                    lowLinks[nodeId] = Math.Min(lowLinks[nodeId], lowLinks[next]);
                }
                else if (onStack.Contains(next))
                {
                    lowLinks[nodeId] = Math.Min(lowLinks[nodeId], indexes[next]);
                }
            }

            if (lowLinks[nodeId] != indexes[nodeId])
            {
                return;
            }

            var component = new HashSet<string>(StringComparer.Ordinal);
            string current;
            do
            {
                current = stack.Pop();
                onStack.Remove(current);
                component.Add(current);
            } while (current != nodeId);

            if (component.Count > 1 || adjacency[nodeId].Contains(nodeId, StringComparer.Ordinal))
            {
                cyclicComponents.Add(component);
            }
        }

        foreach (var nodeId in nodeIds)
        {
            if (!indexes.ContainsKey(nodeId))
            {
                Visit(nodeId);
            }
        }

        return cyclicComponents;
    }
}
