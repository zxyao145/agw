using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Repositories;
using Agw.Agents.Definitions.Domain.Topology;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Providers.Contracts.References;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Domain.Services;

/// <summary>
/// <para>Agentflow 的图只能引用所有者自己的 Agent 与其他 Agentflow，且嵌套引用不能回到自身；摘要模型供应商的可见性作为事实交给 AgentflowBehavior。</para>
/// <para>An Agentflow graph may reference only the owner's agents and other Agentflows, and nested references must not lead back to itself; the summary model provider's visibility is handed to AgentflowBehavior as a fact.</para>
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

    public async Task<bool> TryDefineGraphAsync(
        Agentflow agentflow,
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        AgentflowConfigurationFacts configuration,
        CancellationToken cancellationToken
    )
    {
        if (await HasNestedCycleAsync(agentflow.Id, nodes, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var agentIds = GetReferencedIds(nodes, AgentflowNodeKind.Agent);
        var agentNames =
            agentIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await _definitions.ListOwnedAgentNamesAsync(agentIds, cancellationToken).ConfigureAwait(false);
        if (agentIds.Any(id => !agentNames.ContainsKey(id)))
        {
            return false;
        }

        if (!await ReferencesOnlyOwnedAgentflowsAsync(agentflow.Id, nodes, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var summaryModelProviderVisible = await IsSummaryModelProviderVisibleAsync(
                agentflow.SummaryModelProviderId,
                cancellationToken
            )
            .ConfigureAwait(false);
        return new AgentflowBehavior(agentflow).TryReplaceGraph(
            nodes,
            edges,
            configuration,
            agentNames,
            summaryModelProviderVisible
        );
    }

    private async Task<bool> HasNestedCycleAsync(
        Guid agentflowId,
        IReadOnlyList<AgentflowNode> nodes,
        CancellationToken cancellationToken
    )
    {
        var nestedAgentflowIds = nodes
            .Where(node => node.Kind == AgentflowNodeKind.WorkflowAsAgent && node.RelateId.HasValue)
            .Select(node => node.RelateId!.Value)
            .Distinct()
            .ToArray();
        var nestedReferences = await _definitions
            .LoadNestedAgentflowReferencesAsync(nestedAgentflowIds, cancellationToken)
            .ConfigureAwait(false);
        return AgentflowReferenceTopology.HasCycle(agentflowId, nestedAgentflowIds, nestedReferences);
    }

    private async Task<bool> ReferencesOnlyOwnedAgentflowsAsync(
        Guid agentflowId,
        IReadOnlyList<AgentflowNode> nodes,
        CancellationToken cancellationToken
    )
    {
        var agentflowIds = GetReferencedIds(nodes, AgentflowNodeKind.WorkflowAsAgent);
        if (agentflowIds.Contains(agentflowId))
        {
            return false;
        }

        if (agentflowIds.Count == 0)
        {
            return true;
        }

        var ownedIds = await _definitions
            .FilterOwnedAgentflowIdsAsync(agentflowIds, cancellationToken)
            .ConfigureAwait(false);
        return agentflowIds.All(ownedIds.Contains);
    }

    private async Task<bool> IsSummaryModelProviderVisibleAsync(
        Guid? modelProviderId,
        CancellationToken cancellationToken
    )
    {
        if (!modelProviderId.HasValue)
        {
            return false;
        }

        var visibleIds = await _modelProviderReferences
            .FilterVisibleModelProviderIdsAsync([modelProviderId.Value], cancellationToken)
            .ConfigureAwait(false);
        return visibleIds.Contains(modelProviderId.Value);
    }

    private static IReadOnlyList<Guid> GetReferencedIds(IReadOnlyList<AgentflowNode> nodes, AgentflowNodeKind kind) =>
        nodes
            .Where(node => node.Kind == kind && node.RelateId.HasValue && node.RelateId.Value != Guid.Empty)
            .Select(node => node.RelateId!.Value)
            .Distinct()
            .ToList();
}
