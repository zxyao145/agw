using Agw.Agents.Contracts.Catalog;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Facades;

public sealed class AgentCatalogFacade : IAgentCatalogFacade, IAgentReferenceFacade
{
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<Agentflow> _agentflowRepository;
    private readonly IRepository<McpServer> _mcpServerRepository;
    private readonly IRepository<AgentSkillRelation> _agentSkillRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserInfoService _userInfoService;

    public AgentCatalogFacade(
        IRepository<Agent> agentRepository,
        IRepository<Agentflow> agentflowRepository,
        IRepository<McpServer> mcpServerRepository,
        IRepository<AgentSkillRelation> agentSkillRepository,
        IUnitOfWork unitOfWork,
        IUserInfoService userInfoService
    )
    {
        _agentRepository = agentRepository;
        _agentflowRepository = agentflowRepository;
        _mcpServerRepository = mcpServerRepository;
        _agentSkillRepository = agentSkillRepository;
        _unitOfWork = unitOfWork;
        _userInfoService = userInfoService;
    }

    public async Task<IReadOnlyList<AgentDescriptor>> ListDiscoverableAsync(
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        var agents = await _agentRepository
            .Queryable.AsNoTracking()
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
        var agent = await _agentRepository
            .Queryable.AsNoTracking()
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
        return await _mcpServerRepository
            .Queryable.AsNoTracking()
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
            AgentRuntimeType.Agent => (
                await _agentRepository
                    .ListAsync(agent => agent.Id == id && agent.CreateBy == ownerUserId)
                    .ConfigureAwait(false)
            ).Count > 0,
            AgentRuntimeType.Agentflow => (
                await _agentflowRepository
                    .ListAsync(agentflow => agentflow.Id == id && agentflow.CreateBy == ownerUserId)
                    .ConfigureAwait(false)
            ).Count > 0,
            _ => false,
        };
    }

    public async Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var agentCount = await _agentRepository
            .Queryable.CountAsync(agent => agent.CreateBy == ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        var agentflowCount = await _agentflowRepository
            .Queryable.CountAsync(agentflow => agentflow.CreateBy == ownerUserId, cancellationToken)
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
        var usedByAgent = await _agentRepository.Queryable.AnyAsync(
            agent =>
                agent.CreateBy == ownerUserId
                && (
                    agent.ModelProviderId.HasValue && ids.Contains(agent.ModelProviderId.Value)
                    || agent.SummaryModelProviderId.HasValue && ids.Contains(agent.SummaryModelProviderId.Value)
                ),
            cancellationToken
        );
        return usedByAgent
            || await _agentflowRepository.Queryable.AnyAsync(
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
        var relations = await _agentSkillRepository
            .Queryable.AsNoTracking()
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
        var relations = await _agentSkillRepository
            .Queryable.Where(relation => relation.SkillId == skillId && relation.Agent!.CreateBy == ownerUserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var relation in relations)
        {
            _agentSkillRepository.Remove(relation);
        }
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
