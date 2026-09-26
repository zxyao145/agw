using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Repositories;
using Agw.Integrations.Contracts.References;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Skills.Contracts.References;

namespace Agw.Agents.Definitions.Domain.Services;

/// <summary>
/// <para>Agent 与 MCP Server、Skill、Connection 之间只能绑定对所有者可见的资源；不再可见的绑定在读取时隐藏。</para>
/// <para>Agents bind only MCP servers, skills and connections visible to the owner; bindings that are no longer visible are hidden on read.</para>
/// </summary>
public sealed class AgentResourceBindingDomainService
{
    private readonly IAgentDefinitionRepository _definitions;
    private readonly ISkillReferenceFacade _skillReferences;
    private readonly IConnectionReferenceFacade _connectionReferences;

    public AgentResourceBindingDomainService(
        IAgentDefinitionRepository definitions,
        ISkillReferenceFacade skillReferences,
        IConnectionReferenceFacade connectionReferences
    )
    {
        _definitions = definitions;
        _skillReferences = skillReferences;
        _connectionReferences = connectionReferences;
    }

    public Task<(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Removed)> PlanMcpToolServerBindingsAsync(
        IReadOnlyCollection<Guid> currentIds,
        IEnumerable<Guid>? requestedIds
    ) => PlanBindingsAsync(currentIds, requestedIds, ids => _definitions.FilterOwnedMcpServerIdsAsync(ids));

    public Task<(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Removed)> PlanSkillBindingsAsync(
        IReadOnlyCollection<Guid> currentIds,
        IEnumerable<Guid>? requestedIds
    ) => PlanBindingsAsync(currentIds, requestedIds, ids => _skillReferences.FilterVisibleSkillIdsAsync(ids));

    /// <summary>
    /// <para>只替换当前所有者拥有的 Connection 绑定，其他用户的绑定保持不变。</para>
    /// <para>Replaces only the connection bindings the current owner owns and keeps other users' bindings.</para>
    /// </summary>
    public async Task<(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Removed)> PlanConnectionBindingsAsync(
        IReadOnlyCollection<Guid> currentIds,
        IEnumerable<Guid>? requestedIds
    )
    {
        var ownedCurrentIds = await _connectionReferences
            .FilterOwnedConnectionIdsAsync(currentIds)
            .ConfigureAwait(false);
        return await PlanBindingsAsync(
                ownedCurrentIds,
                requestedIds,
                ids => _connectionReferences.FilterOwnedConnectionIdsAsync(ids)
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <para>MCP Server 只能绑定所有者自己的 Agent。</para>
    /// <para>An MCP server binds only the owner's own agents.</para>
    /// </summary>
    public Task<(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Removed)> PlanMcpServerAgentBindingsAsync(
        IReadOnlyCollection<Guid> currentAgentIds,
        IEnumerable<Guid>? requestedAgentIds
    ) => PlanBindingsAsync(currentAgentIds, requestedAgentIds, ids => _definitions.FilterOwnedAgentIdsAsync(ids));

    public async Task RetainVisibleRelationsAsync(IReadOnlyList<Agent> agents)
    {
        var visibleSkillIds = await FilterVisibleAsync(
                agents.SelectMany(agent => agent.AgentSkillRelations).Select(relation => relation.SkillId),
                ids => _skillReferences.FilterVisibleSkillIdsAsync(ids)
            )
            .ConfigureAwait(false);
        var visibleConnectionIds = await FilterVisibleAsync(
                agents.SelectMany(agent => agent.AgentConnectionRelations).Select(relation => relation.ConnectionId),
                ids => _connectionReferences.FilterOwnedConnectionIdsAsync(ids)
            )
            .ConfigureAwait(false);

        foreach (var agent in agents)
        {
            new AgentBehavior(agent).RetainVisibleRelations(visibleSkillIds, visibleConnectionIds);
        }
    }

    private static async Task<(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Removed)> PlanBindingsAsync(
        IReadOnlyCollection<Guid> currentIds,
        IEnumerable<Guid>? requestedIds,
        Func<IReadOnlyCollection<Guid>, Task<IReadOnlySet<Guid>>> filterVisible
    )
    {
        var requested = (requestedIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        var visible = requested.Count == 0 ? [] : (await filterVisible(requested).ConfigureAwait(false)).ToList();
        if (visible.Count != requested.Count)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        return (visible.Except(currentIds).ToList(), currentIds.Except(visible).ToList());
    }

    private static async Task<IReadOnlySet<Guid>> FilterVisibleAsync(
        IEnumerable<Guid> ids,
        Func<IReadOnlyCollection<Guid>, Task<IReadOnlySet<Guid>>> filterVisible
    )
    {
        var candidates = ids.Where(id => id != Guid.Empty).Distinct().ToArray();
        return candidates.Length == 0 ? new HashSet<Guid>() : await filterVisible(candidates).ConfigureAwait(false);
    }
}
