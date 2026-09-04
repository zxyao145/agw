using Agw.Auth.Contracts;
using Agw.Integrations.Contracts.References;
using Agw.Providers.Contracts.References;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Pagination;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Skills.Contracts.References;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Agents;

public sealed record AgentModelRuntimeConfiguration(
    ModelProviderModelSnapshot Model,
    ModelProviderProviderSnapshot Provider
);

public class AgentAppService
{
    private static readonly (AgentUpdateField Field, string JsonName)[] ExternalUnsupportedFields =
    [
        (AgentUpdateField.SystemPrompt, "systemPrompt"),
        (AgentUpdateField.Tools, "tools"),
        (AgentUpdateField.SkillIds, "skillIds"),
        (AgentUpdateField.McpToolServerIds, "mcpToolServerIds"),
        (AgentUpdateField.ConnectionIds, "connectionIds"),
        (AgentUpdateField.EnableSummary, "enableSummary"),
        (AgentUpdateField.SummaryModelProviderId, "summaryModelProviderId"),
    ];

    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<AgentConnectionRelation> _agentConnectionRelationRepository;
    private readonly IConnectionReferenceFacade _connectionReferences;
    private readonly IModelProviderReferenceFacade _modelProviderReferences;
    private readonly IRepository<McpServer> _mcpToolServerRepository;
    private readonly IRepository<AgentMcpServerRelation> _agentMcpToolServerRepository;
    private readonly ISkillReferenceFacade _skillReferences;
    private readonly IRepository<AgentSkillRelation> _agentSkillRelationRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AgentDomainService _agentDomainService;
    private readonly IUserInfoService _userInfoService;

    public AgentAppService(
        IRepository<Agent> agentRepository,
        IRepository<AgentConnectionRelation> agentConnectionRelationRepository,
        IConnectionReferenceFacade connectionReferences,
        IModelProviderReferenceFacade modelProviderReferences,
        IRepository<McpServer> mcpToolServerRepository,
        IRepository<AgentMcpServerRelation> agentMcpToolServerRepository,
        ISkillReferenceFacade skillReferences,
        IRepository<AgentSkillRelation> agentSkillRelationRepository,
        IUnitOfWork unitOfWork,
        AgentDomainService agentDomainService,
        IUserInfoService userInfoService
    )
    {
        _agentRepository = agentRepository;
        _agentConnectionRelationRepository = agentConnectionRelationRepository;
        _connectionReferences = connectionReferences;
        _modelProviderReferences = modelProviderReferences;
        _mcpToolServerRepository = mcpToolServerRepository;
        _agentMcpToolServerRepository = agentMcpToolServerRepository;
        _skillReferences = skillReferences;
        _agentSkillRelationRepository = agentSkillRelationRepository;
        _unitOfWork = unitOfWork;
        _agentDomainService = agentDomainService;
        _userInfoService = userInfoService;
    }

    public async Task<IReadOnlyList<Agent>> ListAgentsAsync()
    {
        var user = _userInfoService.RequiredUserId;
        var agents = await CreateAgentQuery(user).ToListAsync();
        await FilterVisibleReferenceRelationsAsync(agents).ConfigureAwait(false);
        return agents.OrderBy(x => x.Name).ThenByDescending(x => x.CreateTime).ToList();
    }

    public async Task<IReadOnlyList<Agent>> ListAgentsForCurrentUserAsync()
    {
        var user = _userInfoService.RequiredUserId;
        var agents = await CreateAgentQuery(user).Where(agent => agent.Enable).ToListAsync();
        await FilterVisibleReferenceRelationsAsync(agents).ConfigureAwait(false);
        return agents.OrderBy(x => x.Name).ThenByDescending(x => x.CreateTime).ToList();
    }

    public async Task<PagedResult<Agent>> ListAgentPageAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    )
    {
        var page = await UpdatedTimePagination.ToPagedResultAsync(
            CreateAgentQuery(_userInfoService.RequiredUserId),
            agent => agent.Id,
            pageIndex,
            pageSize,
            cancellationToken
        );
        await FilterVisibleReferenceRelationsAsync(page.Items).ConfigureAwait(false);
        return page;
    }

    public async Task<PagedResult<Agent>> ListAgentPageForCurrentUserAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    )
    {
        var page = await UpdatedTimePagination.ToPagedResultAsync(
            CreateAgentQuery(_userInfoService.RequiredUserId),
            agent => agent.Id,
            pageIndex,
            pageSize,
            cancellationToken
        );
        await FilterVisibleReferenceRelationsAsync(page.Items).ConfigureAwait(false);
        return page;
    }

    public async Task<Agent?> GetAgentAsync(Guid id)
    {
        var user = _userInfoService.RequiredUserId;
        var agent = await CreateAgentQuery(user).FirstOrDefaultAsync(agent => agent.Id == id);
        if (agent != null)
        {
            await FilterVisibleReferenceRelationsAsync([agent]).ConfigureAwait(false);
        }
        return agent;
    }

    public async Task<Agent?> GetAgentForCurrentUserAsync(Guid id)
    {
        var user = _userInfoService.RequiredUserId;
        var agent = await CreateAgentQuery(user).FirstOrDefaultAsync(agent => agent.Id == id);
        if (agent != null)
        {
            await FilterVisibleReferenceRelationsAsync([agent]).ConfigureAwait(false);
        }
        return agent;
    }

    public async Task<AgentModelRuntimeConfiguration?> GetModelRuntimeConfigurationAsync(Guid modelProviderId)
    {
        var snapshot = await _modelProviderReferences.GetRuntimeSnapshotAsync(modelProviderId).ConfigureAwait(false);
        return snapshot == null ? null : new AgentModelRuntimeConfiguration(snapshot.Model, snapshot.Provider);
    }

    public async Task<IReadOnlyList<McpServer>> ListEnabledMcpToolServersByAgentAsync(Guid agentId)
    {
        var user = _userInfoService.RequiredUserId;
        var agentExists = await _agentRepository.ListAsync(x => x.Id == agentId && x.CreateBy == user);
        if (agentExists.Count == 0)
        {
            return [];
        }

        var links = await _agentMcpToolServerRepository.ListAsync(x => x.AgentId == agentId);
        return await ListEnabledMcpToolServersAsync(links.Select(x => x.McpToolServerId));
    }

    public async Task<IReadOnlyList<McpServer>> ListEnabledMcpToolServersAsync(IEnumerable<Guid>? mcpToolServerIds)
    {
        var serverIds = (mcpToolServerIds ?? []).Where(static id => id != Guid.Empty).Distinct().ToList();
        if (serverIds.Count == 0)
        {
            return [];
        }

        var user = _userInfoService.RequiredUserId;
        return await _mcpToolServerRepository.ListAsync(x =>
            x.Enabled && serverIds.Contains(x.Id) && x.CreateBy == user
        );
    }

    public async Task<IReadOnlyList<SkillReferenceSnapshot>> ListSkillsByAgentAsync(Guid agentId)
    {
        var user = _userInfoService.RequiredUserId;
        var agentExists = await _agentRepository.ListAsync(x => x.Id == agentId && x.CreateBy == user);
        if (agentExists.Count == 0)
        {
            return [];
        }

        var relations = await _agentSkillRelationRepository.ListAsync(x => x.AgentId == agentId);
        return await ListSkillsAsync(relations.Select(x => x.SkillId));
    }

    public async Task<IReadOnlyList<SkillReferenceSnapshot>> ListSkillsAsync(IEnumerable<Guid>? skillIds)
    {
        var requestedSkillIds = (skillIds ?? []).Where(static id => id != Guid.Empty).Distinct().ToList();
        if (requestedSkillIds.Count == 0)
        {
            return [];
        }

        return await _skillReferences.ResolveVisibleSkillsAsync(requestedSkillIds).ConfigureAwait(false);
    }

    public async Task<Agent?> CreateAgentAsync(
        Agent agent,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? connectionIds
    )
    {
        var user = _userInfoService.RequiredUserId;
        if (
            await HasInvalidModelProviderAsync(agent.ModelProviderId)
            || await HasInvalidModelProviderAsync(agent.SummaryModelProviderId)
        )
        {
            return null;
        }

        _agentDomainService.PrepareForCreate(agent, user);
        await _agentRepository.AddAsync(agent);
        await SyncAgentMcpToolServerRelationsAsync(agent.Id, mcpToolServerIds, user);
        await SyncAgentSkillRelationsAsync(agent.Id, skillIds);
        await SyncAgentConnectionRelationsAsync(agent.Id, connectionIds);
        await _unitOfWork.SaveChangesAsync();
        return agent;
    }

    public async Task<Agent?> UpdateAgentAsync(Guid id, AgentUpdateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = _userInfoService.RequiredUserId;

        var existing = (
            await _agentRepository.ListAsync(agent => agent.Id == id && agent.CreateBy == user)
        ).FirstOrDefault();
        if (existing == null)
        {
            return null;
        }

        if (existing.Type == AgentType.External)
        {
            ValidateExternalAgentUpdate(command);
            _agentDomainService.ApplyUpdate(existing, agent => ApplyExternalAgentUpdate(agent, command), user);
        }
        else
        {
            ValidateSystemAgentUpdate(command);
            _agentDomainService.ApplyUpdate(existing, agent => ApplySystemAgentUpdate(agent, command), user);
        }

        if (
            await HasInvalidModelProviderAsync(existing.ModelProviderId)
            || await HasInvalidModelProviderAsync(existing.SummaryModelProviderId)
        )
        {
            return null;
        }

        _agentRepository.Update(existing);
        if (existing.Type == AgentType.System)
        {
            await SyncAgentMcpToolServerRelationsAsync(existing.Id, command.McpToolServerIds, user);
            await SyncAgentSkillRelationsAsync(existing.Id, command.SkillIds);
            if (command.IsSpecified(AgentUpdateField.ConnectionIds))
            {
                await SyncAgentConnectionRelationsAsync(existing.Id, command.ConnectionIds);
            }
        }

        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<Agent?> UpdateAgentEnabledAsync(
        Guid id,
        bool enable,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        var existing = await _agentRepository.Queryable.FirstOrDefaultAsync(
            agent => agent.Id == id && agent.CreateBy == ownerUserId,
            cancellationToken
        );
        if (existing == null)
        {
            return null;
        }

        existing.Enable = enable;
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> DeleteAgentAsync(Guid id)
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        var existing = (
            await _agentRepository.ListAsync(agent => agent.Id == id && agent.CreateBy == ownerUserId)
        ).FirstOrDefault();
        if (existing == null)
        {
            return false;
        }

        var skillRelations = await _agentSkillRelationRepository.ListAsync(x => x.AgentId == id);
        foreach (var relation in skillRelations)
        {
            _agentSkillRelationRepository.Remove(relation);
        }

        var connectionRelations = await _agentConnectionRelationRepository.ListAsync(x => x.AgentId == id);
        foreach (var relation in connectionRelations)
        {
            _agentConnectionRelationRepository.Remove(relation);
        }

        var mcpRelations = await _agentMcpToolServerRepository.ListAsync(x => x.AgentId == id);
        foreach (var relation in mcpRelations)
        {
            _agentMcpToolServerRepository.Remove(relation);
        }

        _agentRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private async Task<bool> HasInvalidModelProviderAsync(Guid? modelProviderId)
    {
        if (!modelProviderId.HasValue)
        {
            return false;
        }

        var visibleIds = await _modelProviderReferences
            .FilterVisibleModelProviderIdsAsync([modelProviderId.Value])
            .ConfigureAwait(false);
        return !visibleIds.Contains(modelProviderId.Value);
    }

    private static void ValidateExternalAgentUpdate(AgentUpdateCommand command)
    {
        var unsupportedFields = ExternalUnsupportedFields
            .Where(field => command.IsSpecified(field.Field))
            .Select(field => field.JsonName)
            .ToArray();
        if (unsupportedFields.Length > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"External agents cannot update fields: {string.Join(", ", unsupportedFields)}."
            );
        }

        if (command.IsSpecified(AgentUpdateField.DisplayName) && command.DisplayName == null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "displayName cannot be null.");
        }

        if (command.IsSpecified(AgentUpdateField.Description) && command.Description == null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "description cannot be null.");
        }
    }

    private static void ValidateSystemAgentUpdate(AgentUpdateCommand command)
    {
        var missingFields = new List<string>();
        if (!command.IsSpecified(AgentUpdateField.DisplayName) || command.DisplayName == null)
        {
            missingFields.Add("displayName");
        }

        if (!command.IsSpecified(AgentUpdateField.Description) || command.Description == null)
        {
            missingFields.Add("description");
        }

        if (!command.IsSpecified(AgentUpdateField.SystemPrompt) || command.SystemPrompt == null)
        {
            missingFields.Add("systemPrompt");
        }

        if (!command.IsSpecified(AgentUpdateField.ModelProviderId))
        {
            missingFields.Add("modelProviderId");
        }

        if (command.IsSpecified(AgentUpdateField.EnableSummary) && command.EnableSummary == null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "enableSummary cannot be null.");
        }

        if (missingFields.Count > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"System agent update requires fields: {string.Join(", ", missingFields)}."
            );
        }
    }

    private static void ApplyExternalAgentUpdate(Agent agent, AgentUpdateCommand command)
    {
        if (command.IsSpecified(AgentUpdateField.DisplayName))
        {
            agent.DisplayName = command.DisplayName!;
        }

        if (command.IsSpecified(AgentUpdateField.Description))
        {
            agent.Description = command.Description!;
        }

        if (command.IsSpecified(AgentUpdateField.ModelProviderId))
        {
            agent.ModelProviderId = command.ModelProviderId;
        }

        if (command.IsSpecified(AgentUpdateField.Extra))
        {
            agent.Extra = command.Extra;
        }

        if (command.IsSpecified(AgentUpdateField.EnvironmentVariables))
        {
            agent.EnvironmentVariables = command.EnvironmentVariables ?? new Dictionary<string, string>();
        }
    }

    private static void ApplySystemAgentUpdate(Agent agent, AgentUpdateCommand command)
    {
        agent.DisplayName = command.DisplayName!;
        agent.Description = command.Description!;
        agent.SystemPrompt = command.SystemPrompt!;
        agent.ModelProviderId = command.ModelProviderId;
        agent.SummaryModelProviderId = command.SummaryModelProviderId;
        agent.EnableSummary = command.EnableSummary ?? false;
        agent.Tools = command.Tools ?? [];
        agent.Extra = command.Extra;
        agent.EnvironmentVariables = command.EnvironmentVariables ?? new Dictionary<string, string>();
    }

    private async Task SyncAgentMcpToolServerRelationsAsync(
        Guid agentId,
        IEnumerable<Guid>? mcpToolServerIds,
        string user
    )
    {
        var existingLinks = await _agentMcpToolServerRepository.ListAsync(x => x.AgentId == agentId);
        foreach (var link in existingLinks)
        {
            _agentMcpToolServerRepository.Remove(link);
        }

        var requestedIds = _agentDomainService.NormalizeMcpToolServerIds(mcpToolServerIds);
        if (requestedIds.Count == 0)
        {
            return;
        }

        var existingServers = await _mcpToolServerRepository.ListAsync(x =>
            requestedIds.Contains(x.Id) && x.CreateBy == user
        );
        if (existingServers.Count != requestedIds.Count)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        foreach (var serverId in existingServers.Select(x => x.Id))
        {
            await _agentMcpToolServerRepository.AddAsync(
                new AgentMcpServerRelation { AgentId = agentId, McpToolServerId = serverId }
            );
        }
    }

    private async Task SyncAgentSkillRelationsAsync(Guid agentId, IEnumerable<Guid>? skillIds)
    {
        var existingLinks = await _agentSkillRelationRepository.ListAsync(x => x.AgentId == agentId);
        foreach (var link in existingLinks)
        {
            _agentSkillRelationRepository.Remove(link);
        }

        var requestedIds = (skillIds ?? []).Where(static id => id != Guid.Empty).Distinct().ToList();
        if (requestedIds.Count == 0)
        {
            return;
        }

        var visibleSkillIds = await _skillReferences.FilterVisibleSkillIdsAsync(requestedIds).ConfigureAwait(false);
        if (visibleSkillIds.Count != requestedIds.Count)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        foreach (var skillId in visibleSkillIds)
        {
            await _agentSkillRelationRepository.AddAsync(
                new AgentSkillRelation { AgentId = agentId, SkillId = skillId }
            );
        }
    }

    private IQueryable<Agent> CreateAgentQuery(string ownerUserId)
    {
        IQueryable<Agent> query = _agentRepository
            .Queryable.Include(agent =>
                agent.AgentMcpToolServers.Where(relation => relation.McpToolServer.CreateBy == ownerUserId)
            )
            .Include(agent => agent.AgentSkillRelations)
            .Include(agent => agent.AgentConnectionRelations)
            .Where(agent => agent.CreateBy == ownerUserId);
        return query.AsNoTracking().AsSplitQuery();
    }

    private async Task FilterVisibleReferenceRelationsAsync(IReadOnlyList<Agent> agents)
    {
        await FilterVisibleSkillRelationsAsync(agents).ConfigureAwait(false);
        await FilterVisibleConnectionRelationsAsync(agents).ConfigureAwait(false);
    }

    private async Task FilterVisibleSkillRelationsAsync(IReadOnlyList<Agent> agents)
    {
        var skillIds = agents
            .SelectMany(agent => agent.AgentSkillRelations)
            .Select(relation => relation.SkillId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (skillIds.Length == 0)
        {
            return;
        }

        var visibleSkillIds = await _skillReferences.FilterVisibleSkillIdsAsync(skillIds).ConfigureAwait(false);
        foreach (var agent in agents)
        {
            agent.AgentSkillRelations = agent
                .AgentSkillRelations.Where(relation => visibleSkillIds.Contains(relation.SkillId))
                .ToList();
        }
    }

    private async Task FilterVisibleConnectionRelationsAsync(IReadOnlyList<Agent> agents)
    {
        var connectionIds = agents
            .SelectMany(agent => agent.AgentConnectionRelations)
            .Select(relation => relation.ConnectionId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (connectionIds.Length == 0)
        {
            return;
        }

        var visibleConnectionIds = await _connectionReferences
            .FilterOwnedConnectionIdsAsync(connectionIds)
            .ConfigureAwait(false);
        foreach (var agent in agents)
        {
            agent.AgentConnectionRelations = agent
                .AgentConnectionRelations.Where(relation => visibleConnectionIds.Contains(relation.ConnectionId))
                .ToList();
        }
    }

    private async Task SyncAgentConnectionRelationsAsync(Guid agentId, IEnumerable<Guid>? connectionIds)
    {
        var existingLinks = await _agentConnectionRelationRepository.ListAsync(link => link.AgentId == agentId);
        var ownedExistingIds = await _connectionReferences
            .FilterOwnedConnectionIdsAsync(existingLinks.Select(link => link.ConnectionId).ToArray())
            .ConfigureAwait(false);
        foreach (var link in existingLinks.Where(link => ownedExistingIds.Contains(link.ConnectionId)))
        {
            _agentConnectionRelationRepository.Remove(link);
        }

        var requestedIds = (connectionIds ?? []).Where(static id => id != Guid.Empty).Distinct().ToList();
        if (requestedIds.Count == 0)
        {
            return;
        }

        var ownedConnectionIds = await _connectionReferences
            .FilterOwnedConnectionIdsAsync(requestedIds)
            .ConfigureAwait(false);
        if (ownedConnectionIds.Count != requestedIds.Count)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        foreach (var connectionId in ownedConnectionIds)
        {
            await _agentConnectionRelationRepository.AddAsync(
                new AgentConnectionRelation { AgentId = agentId, ConnectionId = connectionId }
            );
        }
    }
}
