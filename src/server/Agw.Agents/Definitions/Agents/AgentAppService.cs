using Agw.Auth.Contracts;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Pagination;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Agents;

public sealed record AgentModelRuntimeConfiguration(
    ModelProviderRelation ModelProvider,
    AgwAiModel Model,
    Provider Provider
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
    private readonly IRepository<Connection> _connectionRepository;
    private readonly IRepository<ModelProviderRelation> _modelProviderRepository;
    private readonly IRepository<AgwAiModel> _modelRepository;
    private readonly IRepository<Provider> _providerRepository;
    private readonly IRepository<McpServer> _mcpToolServerRepository;
    private readonly IRepository<AgentMcpServerRelation> _agentMcpToolServerRepository;
    private readonly IRepository<Skill> _skillRepository;
    private readonly IRepository<AgentSkillRelation> _agentSkillRelationRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AgentDomainService _agentDomainService;
    private readonly IUserInfoService _userInfoService;

    public AgentAppService(
        IRepository<Agent> agentRepository,
        IRepository<AgentConnectionRelation> agentConnectionRelationRepository,
        IRepository<Connection> connectionRepository,
        IRepository<ModelProviderRelation> modelProviderRepository,
        IRepository<AgwAiModel> modelRepository,
        IRepository<Provider> providerRepository,
        IRepository<McpServer> mcpToolServerRepository,
        IRepository<AgentMcpServerRelation> agentMcpToolServerRepository,
        IRepository<Skill> skillRepository,
        IRepository<AgentSkillRelation> agentSkillRelationRepository,
        IUnitOfWork unitOfWork,
        AgentDomainService agentDomainService,
        IUserInfoService userInfoService
    )
    {
        _agentRepository = agentRepository;
        _agentConnectionRelationRepository = agentConnectionRelationRepository;
        _connectionRepository = connectionRepository;
        _modelProviderRepository = modelProviderRepository;
        _modelRepository = modelRepository;
        _providerRepository = providerRepository;
        _mcpToolServerRepository = mcpToolServerRepository;
        _agentMcpToolServerRepository = agentMcpToolServerRepository;
        _skillRepository = skillRepository;
        _agentSkillRelationRepository = agentSkillRelationRepository;
        _unitOfWork = unitOfWork;
        _agentDomainService = agentDomainService;
        _userInfoService = userInfoService;
    }

    public async Task<IReadOnlyList<Agent>> ListAgentsAsync()
    {
        var user = _userInfoService.RequiredUserId;
        var agents = await CreateAgentQuery(user).ToListAsync();
        await FilterVisibleSkillRelationsAsync(agents, user).ConfigureAwait(false);
        return agents.OrderBy(x => x.Name).ThenByDescending(x => x.CreateTime).ToList();
    }

    public async Task<IReadOnlyList<Agent>> ListAgentsForCurrentUserAsync()
    {
        var user = _userInfoService.RequiredUserId;
        var agents = await CreateAgentQuery(user).Where(agent => agent.Enable).ToListAsync();
        await FilterVisibleSkillRelationsAsync(agents, user).ConfigureAwait(false);
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
        await FilterVisibleSkillRelationsAsync(page.Items, _userInfoService.RequiredUserId).ConfigureAwait(false);
        return page;
    }

    public async Task<PagedResult<Agent>> ListAgentPageForCurrentUserAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    )
    {
        var user = _userInfoService.RequiredUserId;
        var page = await UpdatedTimePagination.ToPagedResultAsync(
            CreateAgentQuery(_userInfoService.RequiredUserId),
            agent => agent.Id,
            pageIndex,
            pageSize,
            cancellationToken
        );
        await FilterVisibleSkillRelationsAsync(page.Items, user).ConfigureAwait(false);
        return page;
    }

    public async Task<Agent?> GetAgentAsync(Guid id)
    {
        var user = _userInfoService.RequiredUserId;
        var agent = await CreateAgentQuery(user).FirstOrDefaultAsync(agent => agent.Id == id);
        if (agent != null)
        {
            await FilterVisibleSkillRelationsAsync([agent], user).ConfigureAwait(false);
        }
        return agent;
    }

    public async Task<Agent?> GetAgentForCurrentUserAsync(Guid id)
    {
        var user = _userInfoService.RequiredUserId;
        var agent = await CreateAgentQuery(user).FirstOrDefaultAsync(agent => agent.Id == id);
        if (agent != null)
        {
            await FilterVisibleSkillRelationsAsync([agent], user).ConfigureAwait(false);
        }
        return agent;
    }

    public async Task<AgentModelRuntimeConfiguration?> GetModelRuntimeConfigurationAsync(Guid modelProviderId)
    {
        var user = _userInfoService.RequiredUserId;
        var modelProvider = await _modelProviderRepository.Queryable.FirstOrDefaultAsync(relation =>
            relation.Id == modelProviderId && relation.CreateBy == user
        );
        if (modelProvider == null)
        {
            return null;
        }

        var model = await _modelRepository.Queryable.FirstOrDefaultAsync(item =>
            item.Id == modelProvider.ModelId && item.CreateBy == user
        );
        var provider = await _providerRepository
            .Queryable.Include(x => x.AuthConfigs)
            .SingleOrDefaultAsync(x => x.Id == modelProvider.ProviderId && x.CreateBy == user);
        return model == null || provider == null
            ? null
            : new AgentModelRuntimeConfiguration(modelProvider, model, provider);
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

    public async Task<IReadOnlyList<Skill>> ListSkillsByAgentAsync(Guid agentId)
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

    public async Task<IReadOnlyList<Skill>> ListSkillsAsync(IEnumerable<Guid>? skillIds)
    {
        var requestedSkillIds = (skillIds ?? []).Where(static id => id != Guid.Empty).Distinct().ToList();
        if (requestedSkillIds.Count == 0)
        {
            return [];
        }

        var user = _userInfoService.RequiredUserId;
        return await _skillRepository.ListAsync(x =>
            requestedSkillIds.Contains(x.Id) && (x.Kind == SkillKind.BuiltIn || x.CreateBy == user)
        );
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
            await HasInvalidModelProviderAsync(agent.ModelProviderId, user)
            || await HasInvalidModelProviderAsync(agent.SummaryModelProviderId, user)
        )
        {
            return null;
        }

        _agentDomainService.PrepareForCreate(agent, user);
        await _agentRepository.AddAsync(agent);
        await SyncAgentMcpToolServerRelationsAsync(agent.Id, mcpToolServerIds, user);
        await SyncAgentSkillRelationsAsync(agent.Id, skillIds, user);
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
            await HasInvalidModelProviderAsync(existing.ModelProviderId, user)
            || await HasInvalidModelProviderAsync(existing.SummaryModelProviderId, user)
        )
        {
            return null;
        }

        _agentRepository.Update(existing);
        if (existing.Type == AgentType.System)
        {
            await SyncAgentMcpToolServerRelationsAsync(existing.Id, command.McpToolServerIds, user);
            await SyncAgentSkillRelationsAsync(existing.Id, command.SkillIds, user);
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

    private async Task<bool> HasInvalidModelProviderAsync(Guid? modelProviderId, string user)
    {
        if (!modelProviderId.HasValue)
        {
            return false;
        }

        return await _modelProviderRepository.SingleOrDefaultAsync(relation =>
                relation.Id == modelProviderId.Value && relation.CreateBy == user
            ) == null;
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

    private async Task SyncAgentSkillRelationsAsync(Guid agentId, IEnumerable<Guid>? skillIds, string user)
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

        var existingSkills = await _skillRepository.ListAsync(x =>
            requestedIds.Contains(x.Id) && (x.Kind == SkillKind.BuiltIn || x.CreateBy == user)
        );
        if (existingSkills.Count != requestedIds.Count)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        foreach (var skillId in existingSkills.Select(x => x.Id))
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
            .Include(agent =>
                agent.AgentConnectionRelations.Where(relation => relation.Connection.CreateBy == ownerUserId)
            )
            .Where(agent => agent.CreateBy == ownerUserId);
        return query.AsNoTracking().AsSplitQuery();
    }

    private async Task FilterVisibleSkillRelationsAsync(IReadOnlyList<Agent> agents, string ownerUserId)
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

        var visibleSkillIds = (
            await _skillRepository
                .ListAsync(skill =>
                    skillIds.Contains(skill.Id) && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId)
                )
                .ConfigureAwait(false)
        )
            .Select(skill => skill.Id)
            .ToHashSet();
        foreach (var agent in agents)
        {
            agent.AgentSkillRelations = agent
                .AgentSkillRelations.Where(relation => visibleSkillIds.Contains(relation.SkillId))
                .ToList();
        }
    }

    private async Task SyncAgentConnectionRelationsAsync(Guid agentId, IEnumerable<Guid>? connectionIds)
    {
        var user = _userInfoService.RequiredUserId;
        var existingLinks = await _agentConnectionRelationRepository.ListAsync(
            link => link.AgentId == agentId,
            null,
            link => link.Connection
        );
        foreach (var link in existingLinks.Where(link => link.Connection.CreateBy == user))
        {
            _agentConnectionRelationRepository.Remove(link);
        }

        var requestedIds = (connectionIds ?? []).Where(static id => id != Guid.Empty).Distinct().ToList();
        if (requestedIds.Count == 0)
        {
            return;
        }

        var connections = await _connectionRepository.ListAsync(x => requestedIds.Contains(x.Id) && x.CreateBy == user);
        if (connections.Count != requestedIds.Count)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        foreach (var connectionId in connections.Select(x => x.Id))
        {
            await _agentConnectionRelationRepository.AddAsync(
                new AgentConnectionRelation { AgentId = agentId, ConnectionId = connectionId }
            );
        }
    }
}
