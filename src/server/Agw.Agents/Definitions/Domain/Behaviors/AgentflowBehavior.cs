using System.Globalization;
using Agw.Agents.Definitions.Domain.Topology;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Domain.Behaviors;

public sealed class AgentflowBehavior
{
    private const string InputNodeId = "input";
    private const string DuplicateIdProblemCode = "duplicate-id";

    private readonly Agentflow _agentflow;

    public AgentflowBehavior(Agentflow agentflow)
    {
        ArgumentNullException.ThrowIfNull(agentflow);
        _agentflow = agentflow;
    }

    public void StartRevisions()
    {
        _agentflow.LatestRevision = 1;
        _agentflow.PublishedRevision = 0;
    }

    public bool TryAdvanceRevision(int baseRevision)
    {
        if (_agentflow.LatestRevision != baseRevision)
        {
            return false;
        }

        _agentflow.LatestRevision = checked(_agentflow.LatestRevision + 1);
        return true;
    }

    public void Publish(int revision) => _agentflow.PublishedRevision = revision;

    public static IReadOnlyList<AgentflowProblem> Validate(string name, AgentflowDefinition definition)
    {
        var problems = new List<AgentflowProblem>();
        var nodes = definition.Nodes;
        var edges = definition.Edges;
        var nodesById = nodes
            .GroupBy(node => node.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var membersByNode = definition
            .Members.GroupBy(member => member.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            problems.Add(
                new AgentflowProblem(
                    "name-required",
                    "The Agentflow needs a name of at most 200 characters.",
                    null,
                    null
                )
            );
        }

        ValidateDuplicateIds(nodes, edges, definition.Members, nodesById, problems);
        ValidateInput(nodes, edges, problems);
        ValidateRunner(nodes, membersByNode, problems);
        ValidateReachability(nodes, edges, nodesById, problems);
        ValidateEdgeEndpoints(edges, nodesById, problems);
        var choicesByNode = ValidateNodeRules(nodes, membersByNode, definition.NodeOptions, problems);
        ValidateEdgeRules(nodesById, edges, choicesByNode, definition.EdgeConditions, problems);
        ValidateCycles(nodes, edges, nodesById, problems);
        ValidateSummaryOutput(nodes, definition.NodeOptions, problems);
        return problems;
    }

    private static void ValidateDuplicateIds(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyList<AgentflowBlockMember> members,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        ICollection<AgentflowProblem> problems
    )
    {
        foreach (
            var group in nodes.GroupBy(node => node.NodeId, StringComparer.Ordinal).Where(group => group.Count() > 1)
        )
        {
            problems.Add(
                new AgentflowProblem(
                    DuplicateIdProblemCode,
                    $"{NodeName(group.First())} is used more than once.",
                    group.Key,
                    null
                )
            );
        }

        foreach (
            var group in edges.GroupBy(edge => edge.EdgeId, StringComparer.Ordinal).Where(group => group.Count() > 1)
        )
        {
            var edge = group.First();
            problems.Add(
                new AgentflowProblem(
                    DuplicateIdProblemCode,
                    $"The connection from {NodeName(nodesById.GetValueOrDefault(edge.SourceNodeId))} to {NodeName(nodesById.GetValueOrDefault(edge.TargetNodeId))} is used more than once.",
                    null,
                    group.Key
                )
            );
        }

        foreach (
            var group in members.GroupBy(member => (member.NodeId, member.MemberId)).Where(group => group.Count() > 1)
        )
        {
            problems.Add(
                new AgentflowProblem(
                    DuplicateIdProblemCode,
                    $"{NodeName(nodesById.GetValueOrDefault(group.Key.NodeId))} has a member that is used more than once.",
                    group.Key.NodeId,
                    null
                )
            );
        }
    }

    private static void ValidateInput(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        ICollection<AgentflowProblem> problems
    )
    {
        var inputNode = nodes.FirstOrDefault(node => node.Kind == AgentflowNodeKind.Input);
        var inputNodes = nodes
            .Where(node => node.NodeId == InputNodeId || node.Kind == AgentflowNodeKind.Input)
            .ToArray();
        var inputNodeId = inputNode?.NodeId ?? inputNodes.FirstOrDefault()?.NodeId ?? InputNodeId;
        if (
            inputNodes.Length != 1
            || inputNodes[0].NodeId != InputNodeId
            || inputNodes[0].Kind != AgentflowNodeKind.Input
            || edges.Any(edge => edge.TargetNodeId == inputNodeId)
        )
        {
            problems.Add(
                new AgentflowProblem(
                    "input-invalid",
                    "The flow needs exactly one Input node with no incoming connections.",
                    inputNodeId,
                    null
                )
            );
        }
    }

    private static void ValidateRunner(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyDictionary<string, AgentflowBlockMember[]> membersByNode,
        ICollection<AgentflowProblem> problems
    )
    {
        var hasRunner =
            nodes.Any(node => node.Kind is AgentflowNodeKind.Agent or AgentflowNodeKind.WorkflowAsAgent)
            || nodes.Any(node =>
                IsBlockNode(node.Kind) && membersByNode.TryGetValue(node.NodeId, out var members) && members.Length > 0
            );
        if (!hasRunner)
        {
            problems.Add(
                new AgentflowProblem(
                    "runner-required",
                    "Add an Agent, a Workflow, or a Block with members.",
                    null,
                    null
                )
            );
        }
    }

    private static void ValidateReachability(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        ICollection<AgentflowProblem> problems
    )
    {
        if (!nodes.Any(node => node.NodeId == InputNodeId && node.Kind == AgentflowNodeKind.Input))
        {
            return;
        }

        var adjacency = nodesById.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (adjacency.ContainsKey(edge.SourceNodeId) && adjacency.ContainsKey(edge.TargetNodeId))
            {
                adjacency[edge.SourceNodeId].Add(edge.TargetNodeId);
            }
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(InputNodeId);
        while (pending.TryDequeue(out var nodeId))
        {
            if (!reachable.Add(nodeId))
            {
                continue;
            }

            foreach (var targetId in adjacency[nodeId])
            {
                pending.Enqueue(targetId);
            }
        }

        foreach (var node in nodes.Where(node => node.NodeId != InputNodeId && !reachable.Contains(node.NodeId)))
        {
            problems.Add(
                new AgentflowProblem(
                    "node-unreachable",
                    $"{NodeName(node)} is not reachable from Input.",
                    node.NodeId,
                    null
                )
            );
        }
    }

    private static void ValidateEdgeEndpoints(
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        ICollection<AgentflowProblem> problems
    )
    {
        foreach (
            var edge in edges.Where(edge =>
                !nodesById.ContainsKey(edge.SourceNodeId) || !nodesById.ContainsKey(edge.TargetNodeId)
            )
        )
        {
            problems.Add(
                new AgentflowProblem(
                    "edge-endpoint-missing",
                    "A connection points to a node that does not exist.",
                    null,
                    edge.EdgeId
                )
            );
        }
    }

    private static Dictionary<string, HashSet<string>> ValidateNodeRules(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyDictionary<string, AgentflowBlockMember[]> membersByNode,
        IReadOnlyDictionary<string, AgentflowNodeOptions> nodeOptions,
        ICollection<AgentflowProblem> problems
    )
    {
        var choicesByNode = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            ValidateRoutingStrategy(node, problems);
            if (node.Kind == AgentflowNodeKind.HumanGate)
            {
                choicesByNode[node.NodeId] = ValidateHumanGateChoices(node, nodeOptions, problems);
            }

            ValidateBlockNode(node, membersByNode, problems);
        }

        return choicesByNode;
    }

    private static void ValidateRoutingStrategy(AgentflowNode node, ICollection<AgentflowProblem> problems)
    {
        var validRouting =
            node.Kind == AgentflowNodeKind.HumanGate
                ? node.RoutingStrategy == AgentflowRoutingStrategy.Choice
                : node.RoutingStrategy
                    is AgentflowRoutingStrategy.Direct
                        or AgentflowRoutingStrategy.FanOut
                        or AgentflowRoutingStrategy.Switch;
        if (validRouting && Enum.IsDefined(node.Kind))
        {
            return;
        }

        problems.Add(
            new AgentflowProblem("routing-invalid", $"{NodeName(node)} cannot hand off this way.", node.NodeId, null)
        );
    }

    private static HashSet<string> ValidateHumanGateChoices(
        AgentflowNode node,
        IReadOnlyDictionary<string, AgentflowNodeOptions> nodeOptions,
        ICollection<AgentflowProblem> problems
    )
    {
        var choices = (nodeOptions.GetValueOrDefault(node.NodeId) as HumanGateOptions)?.Choices ?? [];
        var ids = choices.Select(choice => choice.ChoiceId).ToArray();
        if (
            choices.Count == 0
            || choices.Any(choice =>
                string.IsNullOrWhiteSpace(choice.ChoiceId) || string.IsNullOrWhiteSpace(choice.Label)
            )
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
        )
        {
            problems.Add(
                new AgentflowProblem(
                    "choice-required",
                    $"{NodeName(node)} needs at least one choice, and every choice needs a label.",
                    node.NodeId,
                    null
                )
            );
        }

        return ids.ToHashSet(StringComparer.Ordinal);
    }

    private static void ValidateBlockNode(
        AgentflowNode node,
        IReadOnlyDictionary<string, AgentflowBlockMember[]> membersByNode,
        ICollection<AgentflowProblem> problems
    )
    {
        if (!IsBlockNode(node.Kind))
        {
            return;
        }

        var members = membersByNode.GetValueOrDefault(node.NodeId, []);
        var minimum = node.Kind == AgentflowNodeKind.ConcurrentBlock ? 1 : 2;
        if (members.Length < minimum)
        {
            var minimumLabel = minimum == 1 ? "one member" : "two members";
            problems.Add(
                new AgentflowProblem(
                    "block-member-count",
                    $"{NodeName(node)} needs at least {minimumLabel}.",
                    node.NodeId,
                    null
                )
            );
        }

        if (node.Kind == AgentflowNodeKind.MagenticBlock && members.Count(member => member.IsManager) != 1)
        {
            problems.Add(
                new AgentflowProblem(
                    "block-manager-invalid",
                    $"{NodeName(node)} needs exactly one manager.",
                    node.NodeId,
                    null
                )
            );
        }
    }

    private static void ValidateEdgeRules(
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, HashSet<string>> choicesByNode,
        IReadOnlyDictionary<string, IReadOnlyList<AgentflowConditionRule>> edgeConditions,
        ICollection<AgentflowProblem> problems
    )
    {
        var invalidRoutingEdges = new HashSet<string>(StringComparer.Ordinal);
        var invalidConditionEdges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in edges.GroupBy(edge => edge.SourceNodeId, StringComparer.Ordinal))
        {
            if (!nodesById.TryGetValue(group.Key, out var source))
            {
                continue;
            }

            var sourceEdges = group.ToArray();
            var outgoing = group.Where(edge => !edge.IsFanInBarrier).ToArray();
            ValidateSwitchRules(source, outgoing, nodesById, edgeConditions, problems);
            ValidateChoiceRules(source, outgoing, choicesByNode, nodesById, problems);
            foreach (var edge in sourceEdges)
            {
                ValidateEdgeRoutingFields(
                    edge,
                    source,
                    nodesById,
                    edgeConditions,
                    invalidRoutingEdges,
                    invalidConditionEdges,
                    problems
                );
            }
        }

        foreach (var edge in edges.Where(edge => !nodesById.ContainsKey(edge.SourceNodeId)))
        {
            var rules = edgeConditions.GetValueOrDefault(edge.EdgeId, []);
            if (rules.Any(rule => !IsValidCondition(rule)))
            {
                AddConditionProblem(edge, null, nodesById, invalidConditionEdges, problems);
            }
        }
    }

    private static void ValidateSwitchRules(
        AgentflowNode source,
        IReadOnlyList<AgentflowEdge> outgoing,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        IReadOnlyDictionary<string, IReadOnlyList<AgentflowConditionRule>> edgeConditions,
        ICollection<AgentflowProblem> problems
    )
    {
        if (source.RoutingStrategy != AgentflowRoutingStrategy.Switch)
        {
            return;
        }

        var cases = outgoing.Where(edge => !edge.IsSwitchDefault).ToArray();
        ValidateSwitchCases(source, outgoing.Count, cases, nodesById, edgeConditions, problems);
        ValidateSwitchDefaults(source, outgoing, problems);
        ValidateSwitchCaseOrders(source, cases, problems);
    }

    private static void ValidateSwitchCases(
        AgentflowNode source,
        int outgoingCount,
        IReadOnlyList<AgentflowEdge> cases,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        IReadOnlyDictionary<string, IReadOnlyList<AgentflowConditionRule>> edgeConditions,
        ICollection<AgentflowProblem> problems
    )
    {
        if (outgoingCount > 0 && cases.Count == 0)
        {
            problems.Add(
                new AgentflowProblem(
                    "switch-case-required",
                    $"{NodeName(source)} needs at least one branch with a condition.",
                    source.NodeId,
                    null
                )
            );
        }

        foreach (var edge in cases)
        {
            if (edgeConditions.GetValueOrDefault(edge.EdgeId, []).Count == 0)
            {
                problems.Add(
                    new AgentflowProblem(
                        "switch-condition-required",
                        $"The branch from {NodeName(source)} to {NodeName(nodesById.GetValueOrDefault(edge.TargetNodeId))} needs a condition.",
                        null,
                        edge.EdgeId
                    )
                );
            }
        }
    }

    private static void ValidateSwitchDefaults(
        AgentflowNode source,
        IReadOnlyList<AgentflowEdge> outgoing,
        ICollection<AgentflowProblem> problems
    )
    {
        if (outgoing.Count(edge => edge.IsSwitchDefault) > 1)
        {
            problems.Add(
                new AgentflowProblem(
                    "switch-default-duplicate",
                    $"{NodeName(source)} has more than one Else branch.",
                    source.NodeId,
                    null
                )
            );
        }
    }

    private static void ValidateSwitchCaseOrders(
        AgentflowNode source,
        IReadOnlyList<AgentflowEdge> cases,
        ICollection<AgentflowProblem> problems
    )
    {
        if (cases.GroupBy(edge => edge.SwitchCaseOrder).Any(order => order.Count() > 1))
        {
            problems.Add(
                new AgentflowProblem(
                    "switch-order-duplicate",
                    $"{NodeName(source)} has two branches in the same position.",
                    source.NodeId,
                    null
                )
            );
        }
    }

    private static void ValidateChoiceRules(
        AgentflowNode source,
        IReadOnlyList<AgentflowEdge> outgoing,
        IReadOnlyDictionary<string, HashSet<string>> choicesByNode,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        ICollection<AgentflowProblem> problems
    )
    {
        if (source.Kind != AgentflowNodeKind.HumanGate)
        {
            return;
        }

        var choices = choicesByNode.GetValueOrDefault(source.NodeId, []);
        foreach (var edge in outgoing.Where(edge => edge.ChoiceId is null || !choices.Contains(edge.ChoiceId)))
        {
            problems.Add(
                new AgentflowProblem(
                    "edge-choice-invalid",
                    $"The connection from {NodeName(source)} to {NodeName(nodesById.GetValueOrDefault(edge.TargetNodeId))} is not bound to a choice of {NodeName(source)}.",
                    null,
                    edge.EdgeId
                )
            );
        }
    }

    private static void ValidateEdgeRoutingFields(
        AgentflowEdge edge,
        AgentflowNode source,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        IReadOnlyDictionary<string, IReadOnlyList<AgentflowConditionRule>> edgeConditions,
        ISet<string> invalidRoutingEdges,
        ISet<string> invalidConditionEdges,
        ICollection<AgentflowProblem> problems
    )
    {
        var rules = edgeConditions.GetValueOrDefault(edge.EdgeId, []);
        var invalidRoutingFields =
            edge.IsFanInBarrier && (edge.IsSwitchDefault || edge.SwitchCaseOrder != 0 || edge.ChoiceId is not null)
            || edge.IsSwitchDefault
                && (source.RoutingStrategy != AgentflowRoutingStrategy.Switch || edge.SwitchCaseOrder != 0)
            || edge.SwitchCaseOrder != 0 && source.RoutingStrategy != AgentflowRoutingStrategy.Switch
            || edge.ChoiceId is not null && source.Kind != AgentflowNodeKind.HumanGate;
        var invalidRules = rules.Any(rule => !IsValidCondition(rule));
        var conditionsDisallowed =
            (edge.IsSwitchDefault || edge.IsFanInBarrier || source.Kind == AgentflowNodeKind.HumanGate)
            && rules.Count > 0;
        if (invalidRoutingFields)
        {
            AddRoutingProblem(edge, source, nodesById, invalidRoutingEdges, problems);
        }

        if (invalidRules || conditionsDisallowed)
        {
            AddConditionProblem(edge, source, nodesById, invalidConditionEdges, problems);
        }
    }

    private static void AddRoutingProblem(
        AgentflowEdge edge,
        AgentflowNode source,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        ISet<string> invalidRoutingEdges,
        ICollection<AgentflowProblem> problems
    )
    {
        if (!invalidRoutingEdges.Add(edge.EdgeId))
        {
            return;
        }

        problems.Add(
            new AgentflowProblem(
                "routing-invalid",
                $"The connection from {NodeName(source)} to {NodeName(nodesById.GetValueOrDefault(edge.TargetNodeId))} has routing fields that cannot be used.",
                null,
                edge.EdgeId
            )
        );
    }

    private static void ValidateCycles(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        ICollection<AgentflowProblem> problems
    )
    {
        var validEdges = edges
            .Where(edge => nodesById.ContainsKey(edge.SourceNodeId) && nodesById.ContainsKey(edge.TargetNodeId))
            .ToArray();
        foreach (var component in AgentflowTopology.FindCyclicComponents(nodesById.Keys.ToArray(), validEdges))
        {
            var firstNode = nodes.First(node => component.Contains(node.NodeId));
            ValidateCycleExit(component, firstNode, validEdges, nodesById, problems);
            ValidateCycleBarriers(component, validEdges, nodesById, problems);
        }
    }

    private static void ValidateCycleExit(
        IReadOnlySet<string> component,
        AgentflowNode firstNode,
        IReadOnlyList<AgentflowEdge> validEdges,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        ICollection<AgentflowProblem> problems
    )
    {
        var hasExit = validEdges.Any(edge =>
            component.Contains(edge.SourceNodeId)
            && !component.Contains(edge.TargetNodeId)
            && !edge.IsFanInBarrier
            && nodesById[edge.SourceNodeId].RoutingStrategy
                is AgentflowRoutingStrategy.Switch
                    or AgentflowRoutingStrategy.Choice
        );
        if (!hasExit)
        {
            problems.Add(
                new AgentflowProblem(
                    "cycle-exit-required",
                    $"The loop through {NodeName(firstNode)} needs a branch or a choice that leaves it.",
                    firstNode.NodeId,
                    null
                )
            );
        }
    }

    private static void ValidateCycleBarriers(
        IReadOnlySet<string> component,
        IReadOnlyList<AgentflowEdge> validEdges,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        ICollection<AgentflowProblem> problems
    )
    {
        foreach (
            var edge in validEdges.Where(edge =>
                edge.IsFanInBarrier
                && component.Contains(edge.TargetNodeId)
                && !component.Contains(edge.SourceNodeId)
                && nodesById[edge.SourceNodeId].Kind != AgentflowNodeKind.Input
            )
        )
        {
            problems.Add(
                new AgentflowProblem(
                    "cycle-barrier-invalid",
                    $"{NodeName(nodesById[edge.TargetNodeId])} is in a loop and cannot wait for {NodeName(nodesById[edge.SourceNodeId])}.",
                    null,
                    edge.EdgeId
                )
            );
        }
    }

    private static void ValidateSummaryOutput(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyDictionary<string, AgentflowNodeOptions> nodeOptions,
        ICollection<AgentflowProblem> problems
    )
    {
        var outputs = nodes.Where(node => node.Kind == AgentflowNodeKind.Output).ToArray();
        var summaryOutput = outputs.FirstOrDefault(node =>
            nodeOptions.GetValueOrDefault(node.NodeId) is OutputOptions { EnableSummary: true }
        );
        if (summaryOutput is not null && outputs.Length != 1)
        {
            problems.Add(
                new AgentflowProblem(
                    "summary-output-count",
                    "Only one Output can exist when a summary is written.",
                    summaryOutput.NodeId,
                    null
                )
            );
        }
    }

    private static bool IsValidCondition(AgentflowConditionRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Value) || rule.Path is null)
        {
            return false;
        }

        return rule.Field switch
        {
            "contains" or "notContains" or "equals" or "author" or "role" => rule.Path.Length == 0,
            "minMessages" => rule.Path.Length == 0
                && int.TryParse(rule.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                && count > 0,
            "result" => rule.Path.Length > 0
                && rule.Path.Split('.').All(segment => !string.IsNullOrWhiteSpace(segment)),
            _ => false,
        };
    }

    private static void AddConditionProblem(
        AgentflowEdge edge,
        AgentflowNode? source,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        ISet<string> invalidConditionEdges,
        ICollection<AgentflowProblem> problems
    )
    {
        if (!invalidConditionEdges.Add(edge.EdgeId))
        {
            return;
        }

        var sourceName = source is null ? NodeName(nodesById.GetValueOrDefault(edge.SourceNodeId)) : NodeName(source);
        problems.Add(
            new AgentflowProblem(
                "condition-invalid",
                $"The connection from {sourceName} to {NodeName(nodesById.GetValueOrDefault(edge.TargetNodeId))} has a condition that cannot be used.",
                null,
                edge.EdgeId
            )
        );
    }

    internal static string NodeName(AgentflowNode? node) =>
        string.IsNullOrWhiteSpace(node?.Name) ? node?.NodeId ?? "Unknown node" : node.Name;

    private static bool IsBlockNode(AgentflowNodeKind kind) =>
        kind
            is AgentflowNodeKind.ConcurrentBlock
                or AgentflowNodeKind.HandoffBlock
                or AgentflowNodeKind.GroupChatBlock
                or AgentflowNodeKind.MagenticBlock;
}
