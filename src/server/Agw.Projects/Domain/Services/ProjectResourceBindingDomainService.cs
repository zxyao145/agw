using Agw.Agents.Contracts.Catalog;
using Agw.Integrations.Contracts.References;
using Agw.Projects.Domain.Behaviors;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Skills.Contracts.References;

namespace Agw.Projects.Domain.Services;

/// <summary>
/// <para>项目只能绑定对其所有者可见的 MCP Server、Skill 与 Connection；不再可见的绑定在读取时隐藏。</para>
/// <para>A project binds only MCP servers, skills and connections visible to its owner; bindings that are no longer visible are hidden on read.</para>
/// </summary>
public sealed class ProjectResourceBindingDomainService
{
    private readonly IAgentCatalogFacade _agentCatalog;
    private readonly ISkillReferenceFacade _skillReferences;
    private readonly IConnectionReferenceFacade _connectionReferences;

    public ProjectResourceBindingDomainService(
        IAgentCatalogFacade agentCatalog,
        ISkillReferenceFacade skillReferences,
        IConnectionReferenceFacade connectionReferences
    )
    {
        _agentCatalog = agentCatalog;
        _skillReferences = skillReferences;
        _connectionReferences = connectionReferences;
    }

    public Task<(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Removed)> PlanMcpToolServerBindingsAsync(
        IReadOnlyCollection<Guid> currentIds,
        IEnumerable<Guid>? requestedIds
    ) => PlanBindingsAsync(currentIds, requestedIds, ids => _agentCatalog.FilterExistingMcpServerIdsAsync(ids));

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

    public async Task RetainVisibleRelationsAsync(IReadOnlyList<Project> projects)
    {
        var visibleMcpToolServerIds = await FilterVisibleAsync(
                projects
                    .SelectMany(project => project.ProjectMcpToolServers)
                    .Select(relation => relation.McpToolServerId),
                ids => _agentCatalog.FilterExistingMcpServerIdsAsync(ids)
            )
            .ConfigureAwait(false);
        var visibleSkillIds = await FilterVisibleAsync(
                projects.SelectMany(project => project.ProjectSkillRelations).Select(relation => relation.SkillId),
                ids => _skillReferences.FilterVisibleSkillIdsAsync(ids)
            )
            .ConfigureAwait(false);
        var visibleConnectionIds = await FilterVisibleAsync(
                projects
                    .SelectMany(project => project.ProjectConnectionRelations)
                    .Select(relation => relation.ConnectionId),
                ids => _connectionReferences.FilterOwnedConnectionIdsAsync(ids)
            )
            .ConfigureAwait(false);

        foreach (var project in projects)
        {
            new ProjectBehavior(project).RetainVisibleRelations(
                visibleMcpToolServerIds,
                visibleSkillIds,
                visibleConnectionIds
            );
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
