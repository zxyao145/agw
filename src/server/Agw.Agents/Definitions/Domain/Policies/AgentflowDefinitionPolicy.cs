using System.Text.Json;
using Agw.Agents.Definitions.Domain.Decisions;
using Agw.Agents.Definitions.Domain.Topology;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Domain.Policies;

public sealed class AgentflowDefinitionPolicy
{
    private const string InputNodeId = "input";

    public AgentflowDefinitionDecision Evaluate(
        IReadOnlyList<AgentflowNode>? nodes,
        IReadOnlyList<AgentflowEdge>? edges,
        Guid agentflowId,
        IReadOnlyCollection<Guid> existingAgentIds,
        Guid? summaryModelProviderId = null,
        IReadOnlyCollection<Guid>? existingModelProviderIds = null,
        IReadOnlyDictionary<Guid, string>? existingAgentNames = null,
        IReadOnlyCollection<Guid>? existingAgentflowIds = null
    )
    {
        var (normalizedNodes, normalizedEdges) = ValidateAndNormalizeGraph(
            nodes,
            edges,
            agentflowId,
            existingAgentIds,
            summaryModelProviderId,
            existingModelProviderIds,
            existingAgentNames,
            existingAgentflowIds
        );
        return new AgentflowDefinitionDecision { Nodes = normalizedNodes, Edges = normalizedEdges };
    }

    private (IReadOnlyList<AgentflowNode>? Nodes, IReadOnlyList<AgentflowEdge>? Edges) ValidateAndNormalizeGraph(
        IReadOnlyList<AgentflowNode>? nodes,
        IReadOnlyList<AgentflowEdge>? edges,
        Guid agentflowId,
        IReadOnlyCollection<Guid> existingAgentIds,
        Guid? summaryModelProviderId = null,
        IReadOnlyCollection<Guid>? existingModelProviderIds = null,
        IReadOnlyDictionary<Guid, string>? existingAgentNames = null,
        IReadOnlyCollection<Guid>? existingAgentflowIds = null
    )
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

        if (existingAgentflowIds != null)
        {
            var agentflowIdSet = existingAgentflowIds.ToHashSet();
            var relatedAgentflowIds = nodes
                .Where(x => x.Kind == AgentflowNodeKind.WorkflowAsAgent)
                .Select(x => x.RelateId)
                .Where(x => x is not null && x.Value != Guid.Empty)
                .Select(x => x!.Value)
                .Distinct()
                .ToList();
            if (relatedAgentflowIds.Any(id => id == agentflowId || !agentflowIdSet.Contains(id)))
            {
                return (null, null);
            }
        }

        var outputNodes = nodes.Where(node => node.Kind == AgentflowNodeKind.Output).ToList();
        var summaryEnabled = false;
        foreach (var outputNode in outputNodes)
        {
            if (!AgentflowTopology.TryReadOutputSummaryEnabled(outputNode.ConfigJson, out var nodeSummaryEnabled))
            {
                return (null, null);
            }

            summaryEnabled |= nodeSummaryEnabled;
        }

        if (
            summaryEnabled
            && (
                outputNodes.Count != 1
                || !summaryModelProviderId.HasValue
                || existingModelProviderIds == null
                || !existingModelProviderIds.Contains(summaryModelProviderId.Value)
            )
        )
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
            if (
                !nodeIds.Contains(edge.SourceNodeId, StringComparer.Ordinal)
                || !nodeIds.Contains(edge.TargetNodeId, StringComparer.Ordinal)
            )
            {
                return (null, null);
            }

            if (!IsValidConditionJson(edge.ConditionJson))
            {
                return (null, null);
            }
        }

        if (!HasValidRoutingStrategies(edges))
        {
            return (null, null);
        }

        if (!IsValidInputRootedGraph(nodes, edges))
        {
            return (null, null);
        }

        if (!HasValidCycleSemantics(nodes, edges))
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
                Name = ResolveNodeName(x, existingAgentNames),
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

    private static string? ResolveNodeName(AgentflowNode node, IReadOnlyDictionary<Guid, string>? existingAgentNames)
    {
        if (
            !string.IsNullOrWhiteSpace(node.Name)
            || node.Kind != AgentflowNodeKind.Agent
            || !node.RelateId.HasValue
            || existingAgentNames == null
        )
        {
            return node.Name;
        }

        return existingAgentNames.TryGetValue(node.RelateId.Value, out var agentName) ? agentName : node.Name;
    }

    private static bool IsValidInputRootedGraph(IReadOnlyList<AgentflowNode> nodes, IReadOnlyList<AgentflowEdge> edges)
    {
        var inputNodes = nodes
            .Where(node => node.NodeId == InputNodeId || node.Kind == AgentflowNodeKind.Input)
            .ToList();
        if (inputNodes.Count != 1)
        {
            return false;
        }

        var inputNode = inputNodes[0];
        if (inputNode.NodeId != InputNodeId || inputNode.Kind != AgentflowNodeKind.Input)
        {
            return false;
        }

        if (edges.Any(edge => edge.TargetNodeId == InputNodeId))
        {
            return false;
        }

        var visibleNodeIds = GetRuntimeVisibleNodeIds(nodes, edges);
        var reachableNodeIds = GetReachableNodeIds(InputNodeId, edges, visibleNodeIds);
        return visibleNodeIds.Where(nodeId => nodeId != InputNodeId).All(reachableNodeIds.Contains);
    }

    private static HashSet<string> GetRuntimeVisibleNodeIds(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges
    )
    {
        var hiddenParticipantIds = GetHiddenBlockParticipantIds(nodes, edges);
        return nodes
            .Where(node => !hiddenParticipantIds.Contains(node.NodeId))
            .Select(node => node.NodeId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> GetReachableNodeIds(
        string startNodeId,
        IReadOnlyList<AgentflowEdge> edges,
        HashSet<string> visibleNodeIds
    )
    {
        var adjacency = visibleNodeIds.ToDictionary(nodeId => nodeId, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (visibleNodeIds.Contains(edge.SourceNodeId) && visibleNodeIds.Contains(edge.TargetNodeId))
            {
                adjacency[edge.SourceNodeId].Add(edge.TargetNodeId);
            }
        }

        var reachableNodeIds = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([startNodeId]);
        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            if (!reachableNodeIds.Add(nodeId))
            {
                continue;
            }

            foreach (var nextNodeId in adjacency.GetValueOrDefault(nodeId) ?? [])
            {
                queue.Enqueue(nextNodeId);
            }
        }

        return reachableNodeIds;
    }

    private static HashSet<string> GetHiddenBlockParticipantIds(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges
    )
    {
        var nodeById = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var edgeNodeIds = edges
            .SelectMany(edge => new[] { edge.SourceNodeId, edge.TargetNodeId })
            .ToHashSet(StringComparer.Ordinal);
        var participantOwnersByNodeId = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var blockNode in nodes.Where(node => IsBlockNode(node.Kind)))
        {
            foreach (var participantNodeId in ReadBlockParticipantNodeIds(blockNode))
            {
                if (!participantOwnersByNodeId.TryGetValue(participantNodeId, out var owners))
                {
                    owners = [];
                    participantOwnersByNodeId[participantNodeId] = owners;
                }

                owners.Add(blockNode.NodeId);
            }
        }

        var hiddenParticipantIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (participantNodeId, ownerBlockIds) in participantOwnersByNodeId)
        {
            if (
                nodeById.TryGetValue(participantNodeId, out var participantNode)
                && IsAgentParticipantKind(participantNode.Kind)
                && ownerBlockIds.Count == 1
                && !edgeNodeIds.Contains(participantNodeId)
            )
            {
                hiddenParticipantIds.Add(participantNodeId);
            }
        }

        return hiddenParticipantIds;
    }

    private static IReadOnlyList<string> ReadBlockParticipantNodeIds(AgentflowNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(node.ConfigJson);
            if (
                !doc.RootElement.TryGetProperty("participantNodeIds", out var participants)
                || participants.ValueKind != JsonValueKind.Array
            )
            {
                return [];
            }

            return participants
                .EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsAgentParticipantKind(AgentflowNodeKind kind)
    {
        return kind is AgentflowNodeKind.Agent or AgentflowNodeKind.WorkflowAsAgent;
    }

    private static bool IsBlockNode(AgentflowNodeKind kind)
    {
        return kind
            is AgentflowNodeKind.ConcurrentBlock
                or AgentflowNodeKind.HandoffBlock
                or AgentflowNodeKind.GroupChatBlock
                or AgentflowNodeKind.MagenticBlock;
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

    private bool HasValidRoutingStrategies(IReadOnlyList<AgentflowEdge> edges)
    {
        foreach (var sourceGroup in edges.GroupBy(edge => edge.SourceNodeId, StringComparer.Ordinal))
        {
            var sourceEdges = sourceGroup.Where(edge => edge.Kind != AgentflowEdgeKind.FanInBarrier).ToList();
            var strategyCount = sourceEdges
                .Select(edge =>
                    edge.Kind switch
                    {
                        AgentflowEdgeKind.Direct => "direct",
                        AgentflowEdgeKind.FanOut => "fan-out",
                        AgentflowEdgeKind.SwitchCase or AgentflowEdgeKind.SwitchDefault => "switch",
                        _ => "unsupported",
                    }
                )
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (
                strategyCount > 1
                || sourceEdges.Any(edge => edge.Kind is < AgentflowEdgeKind.Direct or > AgentflowEdgeKind.SwitchDefault)
            )
            {
                return false;
            }

            var switchEdges = sourceEdges
                .Where(edge => edge.Kind is AgentflowEdgeKind.SwitchCase or AgentflowEdgeKind.SwitchDefault)
                .ToList();
            if (switchEdges.Count == 0)
            {
                continue;
            }

            var cases = switchEdges.Where(edge => edge.Kind == AgentflowEdgeKind.SwitchCase).ToList();
            var defaults = switchEdges.Where(edge => edge.Kind == AgentflowEdgeKind.SwitchDefault).ToList();
            if (
                cases.Count == 0
                || defaults.Count > 1
                || defaults.Any(edge => !string.IsNullOrWhiteSpace(edge.ConditionJson))
            )
            {
                return false;
            }

            var orders = new HashSet<int>();
            foreach (var edge in cases)
            {
                if (
                    string.IsNullOrWhiteSpace(edge.ConditionJson)
                    || !AgentflowTopology.TryReadSwitchCaseOrder(edge.ConfigJson, out var order)
                    || !orders.Add(order)
                )
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsValidConditionProperty(JsonProperty property)
    {
        return property.Name switch
        {
            "always" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "contains" or "notContains" or "equals" or "author" or "role" => property.Value.ValueKind
                == JsonValueKind.String,
            "minMessages" => property.Value.ValueKind == JsonValueKind.Number,
            _ => false,
        };
    }

    private bool HasValidCycleSemantics(IReadOnlyList<AgentflowNode> nodes, IReadOnlyList<AgentflowEdge> edges)
    {
        var cyclicComponents = AgentflowTopology.FindCyclicComponents(
            nodes.Select(node => node.NodeId).ToList(),
            edges
        );
        if (cyclicComponents.Count == 0)
        {
            return true;
        }

        var nodeById = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        foreach (var component in cyclicComponents)
        {
            var hasConditionalExit = edges.Any(edge =>
                component.Contains(edge.SourceNodeId)
                && !component.Contains(edge.TargetNodeId)
                && edge.Kind is AgentflowEdgeKind.SwitchCase or AgentflowEdgeKind.SwitchDefault
            );
            if (!hasConditionalExit)
            {
                return false;
            }

            var outsideBarrierSources = edges
                .Where(edge =>
                    edge.Kind == AgentflowEdgeKind.FanInBarrier
                    && component.Contains(edge.TargetNodeId)
                    && !component.Contains(edge.SourceNodeId)
                )
                .Select(edge => nodeById[edge.SourceNodeId]);
            if (outsideBarrierSources.Any(node => node.NodeId != InputNodeId || node.Kind != AgentflowNodeKind.Input))
            {
                return false;
            }
        }

        return true;
    }
}
