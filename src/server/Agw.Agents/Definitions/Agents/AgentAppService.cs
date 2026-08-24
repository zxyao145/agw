using Agw.Auth.Contracts;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Pagination;
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
        var agents = await CreateAgentQuery().ToListAsync();
        return agents.OrderBy(x => x.Name).ThenByDescending(x => x.CreateTime).ToList();
    }

    public async Task<IReadOnlyList<Agent>> ListAgentsForCurrentUserAsync()
    {
        var agents = await CreateAgentQuery(_userInfoService.RequiredUserId).ToListAsync();
        return agents.OrderBy(x => x.Name).ThenByDescending(x => x.CreateTime).ToList();
    }

    public Task<PagedResult<Agent>> ListAgentPageAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    )
    {
        return UpdatedTimePagination.ToPagedResultAsync(
            CreateAgentQuery(),
            agent => agent.Id,
            pageIndex,
            pageSize,
            cancellationToken
        );
    }

    public Task<PagedResult<Agent>> ListAgentPageForCurrentUserAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    )
    {
        return UpdatedTimePagination.ToPagedResultAsync(
            CreateAgentQuery(_userInfoService.RequiredUserId),
            agent => agent.Id,
            pageIndex,
            pageSize,
            cancellationToken
        );
    }

    public async Task<Agent?> GetAgentAsync(Guid id)
    {
        return await CreateAgentQuery().FirstOrDefaultAsync(agent => agent.Id == id);
    }

    public Task<Agent?> GetAgentForCurrentUserAsync(Guid id) =>
        CreateAgentQuery(_userInfoService.RequiredUserId).FirstOrDefaultAsync(agent => agent.Id == id);

    public async Task<AgentModelRuntimeConfiguration?> GetModelRuntimeConfigurationAsync(Guid modelProviderId)
    {
        var modelProvider = await _modelProviderRepository.GetByIdAsync(modelProviderId);
        if (modelProvider == null)
        {
            return null;
        }

        var model = await _modelRepository.GetByIdAsync(modelProvider.ModelId);
        var provider = await _providerRepository
            .Queryable.Include(x => x.AuthConfigs)
            .SingleOrDefaultAsync(x => x.Id == modelProvider.ProviderId);
        return model == null || provider == null
            ? null
            : new AgentModelRuntimeConfiguration(modelProvider, model, provider);
    }

    public async Task<IReadOnlyList<McpServer>> ListEnabledMcpToolServersByAgentAsync(Guid agentId)
    {
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

        return await _mcpToolServerRepository.ListAsync(x => x.Enabled && serverIds.Contains(x.Id));
    }

    public async Task<IReadOnlyList<Skill>> ListSkillsByAgentAsync(Guid agentId)
    {
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

        return await _skillRepository.ListAsync(x => requestedSkillIds.Contains(x.Id));
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
        await SyncAgentMcpToolServerRelationsAsync(agent.Id, mcpToolServerIds);
        await SyncAgentSkillRelationsAsync(agent.Id, skillIds);
        await SyncAgentConnectionRelationsAsync(agent.Id, connectionIds);
        await _unitOfWork.SaveChangesAsync();
        return agent;
    }

    public async Task<Agent?> UpdateAgentAsync(Guid id, AgentUpdateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = _userInfoService.RequiredUserId;

        var existing = await _agentRepository.GetByIdAsync(id);
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
            await SyncAgentMcpToolServerRelationsAsync(existing.Id, command.McpToolServerIds);
            await SyncAgentSkillRelationsAsync(existing.Id, command.SkillIds);
            if (command.IsSpecified(AgentUpdateField.ConnectionIds))
            {
                await SyncAgentConnectionRelationsAsync(existing.Id, command.ConnectionIds);
            }
        }

        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAgentAsync(Guid id)
    {
        var existing = await _agentRepository.GetByIdAsync(id);
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

        return await _modelProviderRepository.GetByIdAsync(modelProviderId.Value) == null;
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

    private async Task SyncAgentMcpToolServerRelationsAsync(Guid agentId, IEnumerable<Guid>? mcpToolServerIds)
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

        var existingServers = await _mcpToolServerRepository.ListAsync(x => requestedIds.Contains(x.Id));
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

        var existingSkills = await _skillRepository.ListAsync(x => requestedIds.Contains(x.Id));
        foreach (var skillId in existingSkills.Select(x => x.Id))
        {
            await _agentSkillRelationRepository.AddAsync(
                new AgentSkillRelation { AgentId = agentId, SkillId = skillId }
            );
        }
    }

    private IQueryable<Agent> CreateAgentQuery(string? connectionOwnerUserId = null)
    {
        IQueryable<Agent> query = _agentRepository
            .Queryable.Include(agent => agent.AgentMcpToolServers)
            .Include(agent => agent.AgentSkillRelations);
        query =
            connectionOwnerUserId == null
                ? query.Include(agent => agent.AgentConnectionRelations)
                : query.Include(agent =>
                    agent.AgentConnectionRelations.Where(relation =>
                        relation.Connection.CreateBy == connectionOwnerUserId
                    )
                );
        return connectionOwnerUserId == null ? query.AsSplitQuery() : query.AsNoTracking().AsSplitQuery();
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
        foreach (var connectionId in connections.Select(x => x.Id))
        {
            await _agentConnectionRelationRepository.AddAsync(
                new AgentConnectionRelation { AgentId = agentId, ConnectionId = connectionId }
            );
        }
    }
}
