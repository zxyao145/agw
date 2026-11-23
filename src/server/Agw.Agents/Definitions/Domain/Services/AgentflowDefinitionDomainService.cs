using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Repositories;
using Agw.Agents.Definitions.Domain.Topology;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Providers.Contracts.References;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Domain.Services;

/// <summary>
/// <para>校验需要查询其他聚合的 Agentflow 定义规则。</para>
/// <para>Validates Agentflow definition rules that require facts from other aggregates.</para>
/// </summary>
public sealed class AgentflowDefinitionDomainService
{
    private readonly IAgentDefinitionRepository _definitions;
    private readonly IModelProviderReferenceFacade _modelProviderReferences;

    public AgentflowDefinitionDomainService(
        IAgentDefinitionRepository definitions,
        IModelProviderReferenceFacade modelProviderReferences
    )
    {
        _definitions = definitions;
        _modelProviderReferences = modelProviderReferences;
    }

    public async Task<IReadOnlyList<AgentflowProblem>> ValidateAsync(
        Guid? agentflowId,
        string name,
        AgentflowDefinition definition,
        CancellationToken cancellationToken
    )
    {
        var problems = AgentflowBehavior.Validate(name, definition).ToList();
        var nodesById = definition
            .Nodes.GroupBy(node => node.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var references = definition
            .Nodes.Where(node => node.Kind is AgentflowNodeKind.Agent or AgentflowNodeKind.WorkflowAsAgent)
            .Select(node => new DefinitionReference(node.NodeId, node.Kind, node.RelateId))
            .Concat(
                definition.Members.Select(member => new DefinitionReference(
                    member.NodeId,
                    member.Kind,
                    member.RelateId
                ))
            )
            .ToArray();
        var ownedAgentflowIds = await ValidateReferencesAsync(references, nodesById, problems, cancellationToken)
            .ConfigureAwait(false);

        await ValidateNestedReferencesAsync(
                agentflowId,
                references,
                nodesById,
                ownedAgentflowIds,
                problems,
                cancellationToken
            )
            .ConfigureAwait(false);
        await ValidateSummaryModelsAsync(definition, problems, cancellationToken).ConfigureAwait(false);
        await ValidateStructuredResultConditionsAsync(definition, nodesById, problems, cancellationToken)
            .ConfigureAwait(false);
        return problems;
    }

    private async Task<IReadOnlySet<Guid>> ValidateReferencesAsync(
        IReadOnlyList<DefinitionReference> references,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        ICollection<AgentflowProblem> problems,
        CancellationToken cancellationToken
    )
    {
        var agentIds = GetReferenceIds(references, AgentflowNodeKind.Agent);
        var agentflowIds = GetReferenceIds(references, AgentflowNodeKind.WorkflowAsAgent);
        var ownedAgentIds = await _definitions.FilterOwnedAgentIdsAsync(agentIds).ConfigureAwait(false);
        var ownedAgentflowIds = await _definitions
            .FilterOwnedAgentflowIdsAsync(agentflowIds, cancellationToken)
            .ConfigureAwait(false);

        AddReferenceProblems(references, nodesById, ownedAgentIds, ownedAgentflowIds, problems);
        return ownedAgentflowIds;
    }

    private static Guid[] GetReferenceIds(IReadOnlyList<DefinitionReference> references, AgentflowNodeKind kind) =>
        references
            .Where(reference => reference.Kind == kind)
            .Select(reference => reference.RelateId)
            .Where(id => id.HasValue && id.Value != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

    private static void AddReferenceProblems(
        IReadOnlyList<DefinitionReference> references,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        IReadOnlySet<Guid> ownedAgentIds,
        IReadOnlySet<Guid> ownedAgentflowIds,
        ICollection<AgentflowProblem> problems
    )
    {
        foreach (var reference in references)
        {
            var exists =
                reference.RelateId is { } relateId
                && relateId != Guid.Empty
                && (
                    reference.Kind switch
                    {
                        AgentflowNodeKind.Agent => ownedAgentIds.Contains(relateId),
                        AgentflowNodeKind.WorkflowAsAgent => ownedAgentflowIds.Contains(relateId),
                        _ => false,
                    }
                );
            if (!exists)
            {
                problems.Add(
                    new AgentflowProblem(
                        "reference-missing",
                        $"{AgentflowBehavior.NodeName(nodesById.GetValueOrDefault(reference.NodeId))} has no agent or workflow selected, or it is no longer available.",
                        reference.NodeId,
                        null
                    )
                );
            }
        }
    }

    private async Task ValidateNestedReferencesAsync(
        Guid? agentflowId,
        IReadOnlyList<DefinitionReference> references,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        IReadOnlySet<Guid> ownedAgentflowIds,
        ICollection<AgentflowProblem> problems,
        CancellationToken cancellationToken
    )
    {
        var publishedRevisions = await _definitions
            .ListOwnedAgentflowPublishedRevisionsAsync(ownedAgentflowIds.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        var publishedTargetIds = ValidatePublishedReferences(
            references,
            nodesById,
            ownedAgentflowIds,
            publishedRevisions,
            problems
        );

        if (publishedTargetIds.Count == 0)
        {
            return;
        }

        var nestedReferences = await _definitions
            .LoadPublishedAgentflowReferencesAsync(publishedTargetIds.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        ValidateNestedCycles(agentflowId, references, nodesById, publishedTargetIds, nestedReferences, problems);
    }

    private static HashSet<Guid> ValidatePublishedReferences(
        IReadOnlyList<DefinitionReference> references,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        IReadOnlySet<Guid> ownedAgentflowIds,
        IReadOnlyDictionary<Guid, int> publishedRevisions,
        ICollection<AgentflowProblem> problems
    )
    {
        var publishedTargetIds = new HashSet<Guid>();
        foreach (var reference in GetNestedReferences(references, ownedAgentflowIds))
        {
            var targetId = reference.RelateId!.Value;
            if (publishedRevisions.GetValueOrDefault(targetId) == 0)
            {
                problems.Add(
                    new AgentflowProblem(
                        "nested-unpublished",
                        $"{AgentflowBehavior.NodeName(nodesById.GetValueOrDefault(reference.NodeId))} uses a workflow that is not published.",
                        reference.NodeId,
                        null
                    )
                );
            }
            else
            {
                publishedTargetIds.Add(targetId);
            }
        }

        return publishedTargetIds;
    }

    private static IEnumerable<DefinitionReference> GetNestedReferences(
        IReadOnlyList<DefinitionReference> references,
        IReadOnlySet<Guid> targetIds
    ) =>
        references.Where(reference =>
            reference.Kind == AgentflowNodeKind.WorkflowAsAgent
            && reference.RelateId.HasValue
            && reference.RelateId.Value != Guid.Empty
            && targetIds.Contains(reference.RelateId.Value)
        );

    private static void ValidateNestedCycles(
        Guid? agentflowId,
        IReadOnlyList<DefinitionReference> references,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        IReadOnlySet<Guid> publishedTargetIds,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>> nestedReferences,
        ICollection<AgentflowProblem> problems
    )
    {
        foreach (var reference in GetNestedReferences(references, publishedTargetIds))
        {
            var targetId = reference.RelateId!.Value;
            var hasCycle = agentflowId.HasValue
                ? AgentflowReferenceTopology.HasCycle(agentflowId.Value, [targetId], nestedReferences)
                : AgentflowReferenceTopology.HasCycle(
                    targetId,
                    nestedReferences.GetValueOrDefault(targetId) ?? [],
                    nestedReferences
                );
            if (hasCycle)
            {
                problems.Add(
                    new AgentflowProblem(
                        "nested-cycle",
                        $"{AgentflowBehavior.NodeName(nodesById.GetValueOrDefault(reference.NodeId))} makes this Agentflow contain itself.",
                        reference.NodeId,
                        null
                    )
                );
            }
        }
    }

    private async Task ValidateSummaryModelsAsync(
        AgentflowDefinition definition,
        ICollection<AgentflowProblem> problems,
        CancellationToken cancellationToken
    )
    {
        var summaryOutputs = definition
            .Nodes.Where(node =>
                node.Kind == AgentflowNodeKind.Output
                && definition.NodeOptions.GetValueOrDefault(node.NodeId) is OutputOptions { EnableSummary: true }
            )
            .ToArray();
        var modelProviderIds = summaryOutputs
            .Select(output => output.SummaryModelProviderId)
            .Where(id => id.HasValue && id.Value != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var visibleModelProviderIds = await _modelProviderReferences
            .FilterVisibleModelProviderIdsAsync(modelProviderIds, cancellationToken)
            .ConfigureAwait(false);

        foreach (var output in summaryOutputs)
        {
            if (
                output.SummaryModelProviderId is not { } modelProviderId
                || modelProviderId == Guid.Empty
                || !visibleModelProviderIds.Contains(modelProviderId)
            )
            {
                problems.Add(
                    new AgentflowProblem(
                        "summary-model-missing",
                        $"{AgentflowBehavior.NodeName(output)} needs a summary model.",
                        output.NodeId,
                        null
                    )
                );
            }
        }
    }

    private async Task ValidateStructuredResultConditionsAsync(
        AgentflowDefinition definition,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        ICollection<AgentflowProblem> problems,
        CancellationToken cancellationToken
    )
    {
        var resultEdges = GetResultConditionEdges(definition);
        var resultAgentIds = resultEdges
            .Select(item => nodesById.GetValueOrDefault(item.Edge.SourceNodeId))
            .Where(node => node?.Kind == AgentflowNodeKind.Agent && node.RelateId.HasValue)
            .Select(node => node!.RelateId!.Value)
            .Distinct()
            .ToArray();
        var structuredAgentIds = await _definitions
            .FilterStructuredResultAgentIdsAsync(resultAgentIds, cancellationToken)
            .ConfigureAwait(false);

        AddStructuredResultProblems(resultEdges, nodesById, structuredAgentIds, problems);
    }

    private static (AgentflowEdge Edge, IReadOnlyList<AgentflowConditionRule> Conditions)[] GetResultConditionEdges(
        AgentflowDefinition definition
    ) =>
        definition
            .Edges.Select(edge =>
                (Edge: edge, Conditions: definition.EdgeConditions.GetValueOrDefault(edge.EdgeId, []))
            )
            .Where(item => item.Conditions.Any(condition => condition.Field == "result"))
            .ToArray();

    private static void AddStructuredResultProblems(
        IReadOnlyList<(AgentflowEdge Edge, IReadOnlyList<AgentflowConditionRule> Conditions)> resultEdges,
        IReadOnlyDictionary<string, AgentflowNode> nodesById,
        IReadOnlySet<Guid> structuredAgentIds,
        ICollection<AgentflowProblem> problems
    )
    {
        foreach (var item in resultEdges)
        {
            var source = nodesById.GetValueOrDefault(item.Edge.SourceNodeId);
            var producesStructuredResult =
                source?.Kind == AgentflowNodeKind.Agent
                && source.RelateId is { } agentId
                && structuredAgentIds.Contains(agentId);
            if (!producesStructuredResult)
            {
                problems.Add(
                    new AgentflowProblem(
                        "condition-result-unavailable",
                        $"The connection from {AgentflowBehavior.NodeName(source)} to {AgentflowBehavior.NodeName(nodesById.GetValueOrDefault(item.Edge.TargetNodeId))} reads a Result, but {AgentflowBehavior.NodeName(source)} does not produce one.",
                        null,
                        item.Edge.EdgeId
                    )
                );
            }
        }
    }

    private sealed record DefinitionReference(string NodeId, AgentflowNodeKind Kind, Guid? RelateId);
}
