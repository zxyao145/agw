using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Catalog;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Facades;

public sealed class AgentCatalogFacade : IAgentCatalogFacade, IAgentReferenceFacade
{
    private readonly IAgentsDbContext _dbContext;
    private readonly IUserInfoService _userInfoService;

    public AgentCatalogFacade(IAgentsDbContext dbContext, IUserInfoService userInfoService)
    {
        _dbContext = dbContext;
        _userInfoService = userInfoService;
    }

    public async Task<IReadOnlyList<AgentDescriptor>> ListDiscoverableAsync(
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        var agents = await _dbContext
            .Agents.AsNoTracking()
            .Where(agent => agent.CreateBy == ownerUserId)
            .OrderBy(agent => agent.Name)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return agents.Select(Map).ToArray();
    }

    public async Task<AgentDescriptor?> FindDiscoverableByNameAsync(
        string name,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedName = name.Trim();
        var ownerUserId = ResolveOwnerUserId();
        var agent = await _dbContext
            .Agents.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Name == normalizedName && item.CreateBy == ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        return agent == null ? null : Map(agent);
    }

    public async Task<IReadOnlySet<Guid>> FilterExistingMcpServerIdsAsync(
        IReadOnlyCollection<Guid> serverIds,
        CancellationToken cancellationToken = default
    )
    {
        if (serverIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = serverIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        var ownerUserId = ResolveOwnerUserId();
        return await _dbContext
            .McpToolServers.AsNoTracking()
            .Where(server => ids.Contains(server.Id) && server.CreateBy == ownerUserId)
            .Select(server => server.Id)
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> IsOwnedTargetAsync(
        AgentRuntimeType type,
        Guid id,
        string ownerUserId,
        CancellationToken cancellationToken = default
    )
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(ownerUserId))
        {
            return false;
        }

        if (
            UserInfoUtil.IsContextActive
            && !string.Equals(UserInfoUtil.RequiredUserId, ownerUserId.Trim(), StringComparison.Ordinal)
        )
        {
            return false;
        }

        return type switch
        {
            AgentRuntimeType.Agent => await _dbContext
                .Agents.AsNoTracking()
                .AnyAsync(agent => agent.Id == id && agent.CreateBy == ownerUserId, cancellationToken),
            AgentRuntimeType.Agentflow => await _dbContext
                .Agentflows.AsNoTracking()
                .AnyAsync(agentflow => agentflow.Id == id && agentflow.CreateBy == ownerUserId, cancellationToken),
            _ => false,
        };
    }

    public async Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var agentCount = await _dbContext
            .Agents.CountAsync(agent => agent.CreateBy == ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        var agentflowCount = await _dbContext
            .Agentflows.CountAsync(agentflow => agentflow.CreateBy == ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        return new AgentCatalogMetrics(agentCount, agentflowCount);
    }

    public async Task<bool> UsesAnyModelProviderAsync(
        IReadOnlyCollection<Guid> modelProviderIds,
        CancellationToken cancellationToken = default
    )
    {
        var ids = modelProviderIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return false;
        }

        var ownerUserId = ResolveOwnerUserId();
        var usedByAgent = await _dbContext.Agents.AnyAsync(
            agent =>
                agent.CreateBy == ownerUserId
                && (
                    agent.ModelProviderId.HasValue && ids.Contains(agent.ModelProviderId.Value)
                    || agent.SummaryModelProviderId.HasValue && ids.Contains(agent.SummaryModelProviderId.Value)
                ),
            cancellationToken
        );
        return usedByAgent
            || await _dbContext.Agentflows.AnyAsync(
                agentflow =>
                    agentflow.CreateBy == ownerUserId
                    && agentflow.SummaryModelProviderId.HasValue
                    && ids.Contains(agentflow.SummaryModelProviderId.Value),
                cancellationToken
            );
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetAgentIdsBySkillIdsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    )
    {
        if (skillIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<Guid>>();
        }

        var ids = skillIds.ToHashSet();
        var ownerUserId = ResolveOwnerUserId();
        var relations = await _dbContext
            .AgentSkillRelations.AsNoTracking()
            .Where(relation => ids.Contains(relation.SkillId) && relation.Agent!.CreateBy == ownerUserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return relations
            .GroupBy(relation => relation.SkillId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)group.Select(relation => relation.AgentId).Distinct().ToArray()
            );
    }

    public async Task RemoveSkillBindingsAsync(Guid skillId, CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var relations = await _dbContext
            .AgentSkillRelations.Where(relation =>
                relation.SkillId == skillId && relation.Agent!.CreateBy == ownerUserId
            )
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var relation in relations)
        {
            _dbContext.AgentSkillRelations.Remove(relation);
        }
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;

    private static AgentDescriptor Map(Agent agent)
    {
        var discoveryDescription =
            string.IsNullOrWhiteSpace(agent.SystemPrompt) ? "An AI agent"
            : agent.SystemPrompt.Length > 200 ? $"{agent.SystemPrompt[..200]}..."
            : agent.SystemPrompt;
        return new AgentDescriptor(agent.Id, agent.Name, agent.DisplayName, discoveryDescription);
    }
}
