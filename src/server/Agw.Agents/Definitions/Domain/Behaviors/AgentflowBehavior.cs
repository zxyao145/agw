using Agw.Agents.Definitions.Domain.Topology;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Domain.Behaviors;

public sealed class AgentflowBehavior
{
    private const string InputNodeId = "input";

    private readonly Agentflow _agentflow;

    public AgentflowBehavior(Agentflow agentflow)
    {
        ArgumentNullException.ThrowIfNull(agentflow);
        _agentflow = agentflow;
    }

    public bool HasValidName() => !string.IsNullOrWhiteSpace(_agentflow.Name);

    /// <summary>
    /// <para>用新的节点与边替换图：图必须从唯一的输入节点出发覆盖全部可见节点，路由与环语义有效，启用摘要时只能有一个输出节点且摘要模型供应商可见。</para>
    /// <para>agentNames 是被引用 Agent 的名称，用于补全未命名的 Agent 节点。</para>
    /// <para>Replaces the graph: it must reach every visible node from the single input node, keep valid routing and cycle semantics, and have one output node and a visible summary model provider when summary is enabled; agentNames fill unnamed Agent nodes.</para>
    /// </summary>
    public bool TryReplaceGraph(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        AgentflowConfigurationFacts configuration,
        IReadOnlyDictionary<Guid, string> agentNames,
        bool summaryModelProviderVisible
    )
    {
        if (!IsValidGraph(nodes, edges, configuration, summaryModelProviderVisible))
        {
            return false;
        }

        var desiredNodes = nodes.Select(node => NormalizeNode(node, agentNames)).ToList();
        var desiredEdges = edges.Select(NormalizeEdge).ToList();
        RemoveObsoleteEdges(desiredEdges);
        ReconcileNodes(desiredNodes);
        ReconcileEdges(desiredEdges);
        return true;
    }

    private bool IsValidGraph(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        AgentflowConfigurationFacts configuration,
        bool summaryModelProviderVisible
    )
    {
        var nodeIds = nodes.Select(node => node.NodeId).ToList();
        if (nodeIds.Count == 0 || nodeIds.Distinct(StringComparer.Ordinal).Count() != nodeIds.Count)
        {
            return false;
        }

        if (!HasValidSummaryOutput(nodes, configuration, summaryModelProviderVisible))
        {
            return false;
        }

        var edgeIds = edges.Select(edge => edge.EdgeId).ToList();
        if (edgeIds.Distinct(StringComparer.Ordinal).Count() != edgeIds.Count)
        {
            return false;
        }

        var hasValidEdges = edges.All(edge =>
            nodeIds.Contains(edge.SourceNodeId, StringComparer.Ordinal)
            && nodeIds.Contains(edge.TargetNodeId, StringComparer.Ordinal)
            && configuration.Conditions[edge.ConditionJson ?? ""]
        );
        return hasValidEdges
            && HasValidRoutingStrategies(edges, configuration)
            && IsValidInputRootedGraph(nodes, edges, configuration)
            && HasValidCycleSemantics(nodes, edges);
    }

    private bool HasValidSummaryOutput(
        IReadOnlyList<AgentflowNode> nodes,
        AgentflowConfigurationFacts configuration,
        bool summaryModelProviderVisible
    )
    {
        var outputNodes = nodes.Where(node => node.Kind == AgentflowNodeKind.Output).ToList();
        var summaryEnabled = false;
        foreach (var outputNode in outputNodes)
        {
            if (configuration.OutputSummary[outputNode.ConfigJson ?? ""] is not { } nodeSummaryEnabled)
            {
                return false;
            }

            summaryEnabled |= nodeSummaryEnabled;
        }

        return !summaryEnabled
            || (outputNodes.Count == 1 && _agentflow.SummaryModelProviderId.HasValue && summaryModelProviderVisible);
    }

    private static bool HasValidRoutingStrategies(
        IReadOnlyList<AgentflowEdge> edges,
        AgentflowConfigurationFacts configuration
    )
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

            if (!HasValidSwitchCases(sourceEdges, configuration))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasValidSwitchCases(
        IReadOnlyList<AgentflowEdge> sourceEdges,
        AgentflowConfigurationFacts configuration
    )
    {
        var switchEdges = sourceEdges
            .Where(edge => edge.Kind is AgentflowEdgeKind.SwitchCase or AgentflowEdgeKind.SwitchDefault)
            .ToList();
        if (switchEdges.Count == 0)
        {
            return true;
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
                || configuration.SwitchOrder[edge.ConfigJson ?? ""] is not { } order
                || !orders.Add(order)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidInputRootedGraph(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        AgentflowConfigurationFacts configuration
    )
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

        var visibleNodeIds = GetRuntimeVisibleNodeIds(nodes, edges, configuration);
        var reachableNodeIds = GetReachableNodeIds(InputNodeId, edges, visibleNodeIds);
        return visibleNodeIds.Where(nodeId => nodeId != InputNodeId).All(reachableNodeIds.Contains);
    }

    private static HashSet<string> GetRuntimeVisibleNodeIds(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        AgentflowConfigurationFacts configuration
    )
    {
        var hiddenParticipantIds = GetHiddenBlockParticipantIds(nodes, edges, configuration);
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

    /// <summary>
    /// <para>只属于一个 Block、且没有连线的 Agent 参与者由 Block 在运行时驱动，不要求从输入节点可达。</para>
    /// <para>An agent participant owned by exactly one Block and not connected by any edge is driven by that Block at runtime, so it need not be reachable from the input node.</para>
    /// </summary>
    private static HashSet<string> GetHiddenBlockParticipantIds(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        AgentflowConfigurationFacts configuration
    )
    {
        var nodeById = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var edgeNodeIds = edges
            .SelectMany(edge => new[] { edge.SourceNodeId, edge.TargetNodeId })
            .ToHashSet(StringComparer.Ordinal);
        var participantOwnersByNodeId = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var blockNode in nodes.Where(node => IsBlockNode(node.Kind)))
        {
            foreach (var participantNodeId in configuration.Participants[blockNode.ConfigJson ?? ""])
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

    private static bool IsAgentParticipantKind(AgentflowNodeKind kind) =>
        kind is AgentflowNodeKind.Agent or AgentflowNodeKind.WorkflowAsAgent;

    private static bool IsBlockNode(AgentflowNodeKind kind) =>
        kind
            is AgentflowNodeKind.ConcurrentBlock
                or AgentflowNodeKind.HandoffBlock
                or AgentflowNodeKind.GroupChatBlock
                or AgentflowNodeKind.MagenticBlock;

    /// <summary>
    /// <para>每个环都必须有条件分支出口；从环外汇入环的屏障边只能来自输入节点。</para>
    /// <para>Every cycle needs a conditional exit, and barrier edges entering a cycle from outside may come only from the input node.</para>
    /// </summary>
    private static bool HasValidCycleSemantics(IReadOnlyList<AgentflowNode> nodes, IReadOnlyList<AgentflowEdge> edges)
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

    private AgentflowNode NormalizeNode(AgentflowNode node, IReadOnlyDictionary<Guid, string> agentNames) =>
        new()
        {
            AgentflowId = _agentflow.Id,
            NodeId = node.NodeId,
            Kind = node.Kind,
            RelateId = node.RelateId,
            Name = ResolveNodeName(node, agentNames),
            PositionJson = node.PositionJson,
            Instructions = node.Instructions,
            ConfigJson = node.ConfigJson,
        };

    private static string? ResolveNodeName(AgentflowNode node, IReadOnlyDictionary<Guid, string> agentNames)
    {
        if (!string.IsNullOrWhiteSpace(node.Name) || node.Kind != AgentflowNodeKind.Agent || !node.RelateId.HasValue)
        {
            return node.Name;
        }

        return agentNames.TryGetValue(node.RelateId.Value, out var agentName) ? agentName : node.Name;
    }

    private AgentflowEdge NormalizeEdge(AgentflowEdge edge) =>
        new()
        {
            AgentflowId = _agentflow.Id,
            EdgeId = edge.EdgeId,
            SourceNodeId = edge.SourceNodeId,
            TargetNodeId = edge.TargetNodeId,
            Kind = edge.Kind,
            Label = edge.Label,
            ConditionJson = edge.ConditionJson,
            ConfigJson = edge.ConfigJson,
        };

    private void ReconcileNodes(IReadOnlyList<AgentflowNode> desiredNodes)
    {
        var desiredNodeIds = desiredNodes.Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        foreach (var current in _agentflow.Nodes.Where(node => !desiredNodeIds.Contains(node.NodeId)).ToList())
        {
            _agentflow.Nodes.Remove(current);
        }

        var currentById = _agentflow.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        foreach (var desired in desiredNodes)
        {
            if (currentById.TryGetValue(desired.NodeId, out var current))
            {
                current.Kind = desired.Kind;
                current.RelateId = desired.RelateId;
                current.Name = desired.Name;
                current.PositionJson = desired.PositionJson;
                current.Instructions = desired.Instructions;
                current.ConfigJson = desired.ConfigJson;
                continue;
            }

            _agentflow.Nodes.Add(desired);
        }
    }

    private void ReconcileEdges(IReadOnlyList<AgentflowEdge> desiredEdges)
    {
        var currentById = _agentflow.Edges.ToDictionary(edge => edge.EdgeId, StringComparer.Ordinal);
        foreach (var desired in desiredEdges)
        {
            if (currentById.TryGetValue(desired.EdgeId, out var current))
            {
                current.SourceNodeId = desired.SourceNodeId;
                current.TargetNodeId = desired.TargetNodeId;
                current.Kind = desired.Kind;
                current.Label = desired.Label;
                current.ConditionJson = desired.ConditionJson;
                current.ConfigJson = desired.ConfigJson;
                continue;
            }

            _agentflow.Edges.Add(desired);
        }
    }

    private void RemoveObsoleteEdges(IReadOnlyList<AgentflowEdge> desiredEdges)
    {
        var desiredEdgeIds = desiredEdges.Select(edge => edge.EdgeId).ToHashSet(StringComparer.Ordinal);
        foreach (var current in _agentflow.Edges.Where(edge => !desiredEdgeIds.Contains(edge.EdgeId)).ToList())
        {
            _agentflow.Edges.Remove(current);
        }
    }
}
