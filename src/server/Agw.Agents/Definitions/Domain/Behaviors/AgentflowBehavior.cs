using Agw.Agents.Definitions.Domain.Decisions;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Domain.Behaviors;

public sealed class AgentflowBehavior
{
    private readonly Agentflow _agentflow;

    public AgentflowBehavior(Agentflow agentflow)
    {
        ArgumentNullException.ThrowIfNull(agentflow);
        _agentflow = agentflow;
    }

    public bool HasValidName() => !string.IsNullOrWhiteSpace(_agentflow.Name);

    public bool TryApplyGraphDecision(AgentflowDefinitionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Nodes is null || decision.Edges is null)
        {
            return false;
        }

        RemoveObsoleteEdges(decision.Edges);
        ReconcileNodes(decision.Nodes);
        ReconcileEdges(decision.Edges);
        return true;
    }

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
