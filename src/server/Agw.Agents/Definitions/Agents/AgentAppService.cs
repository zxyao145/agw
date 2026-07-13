using System.Text.Json;

using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;

using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Agents;

public sealed record AgentModelRuntimeConfiguration(
    ModelProviderRelation ModelProvider,
    LlmModel Model,
    Provider Provider);

public class AgentAppService
{
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<AgentAppRelation> _agentAppRelationRepository;
    private readonly IRepository<AppInstance> _appInstanceRepository;
    private readonly IRepository<AppDefinition> _appDefinitionRepository;
    private readonly IRepository<ModelProviderRelation> _modelProviderRepository;
    private readonly IRepository<LlmModel> _modelRepository;
    private readonly IRepository<Provider> _providerRepository;
    private readonly IRepository<McpServer> _mcpToolServerRepository;
    private readonly IRepository<AgentMcpServerRelation> _agentMcpToolServerRepository;
    private readonly IRepository<Skill> _skillRepository;
    private readonly IRepository<AgentSkillRelation> _agentSkillRelationRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AgentDomainService _agentDomainService;

    public AgentAppService(
        IRepository<Agent> agentRepository,
        IRepository<AgentAppRelation> agentAppRelationRepository,
        IRepository<AppInstance> appInstanceRepository,
        IRepository<AppDefinition> appDefinitionRepository,
        IRepository<ModelProviderRelation> modelProviderRepository,
        IRepository<LlmModel> modelRepository,
        IRepository<Provider> providerRepository,
        IRepository<McpServer> mcpToolServerRepository,
        IRepository<AgentMcpServerRelation> agentMcpToolServerRepository,
        IRepository<Skill> skillRepository,
        IRepository<AgentSkillRelation> agentSkillRelationRepository,
        IUnitOfWork unitOfWork,
        AgentDomainService agentDomainService)
    {
        _agentRepository = agentRepository;
        _agentAppRelationRepository = agentAppRelationRepository;
        _appInstanceRepository = appInstanceRepository;
        _appDefinitionRepository = appDefinitionRepository;
        _modelProviderRepository = modelProviderRepository;
        _modelRepository = modelRepository;
        _providerRepository = providerRepository;
        _mcpToolServerRepository = mcpToolServerRepository;
        _agentMcpToolServerRepository = agentMcpToolServerRepository;
        _skillRepository = skillRepository;
        _agentSkillRelationRepository = agentSkillRelationRepository;
        _unitOfWork = unitOfWork;
        _agentDomainService = agentDomainService;
    }

    public async Task<IReadOnlyList<Agent>> ListAgentsAsync()
    {
        var agents = await _agentRepository.ListAsync(
            null,
            null,
            x => x.AgentMcpToolServers,
            x => x.AgentSkillRelations,
            x => x.AgentAppRelations);
        return agents
            .OrderBy(x => x.Name)
            .ThenByDescending(x => x.CreateTime)
            .ToList();
    }

    public async Task<Agent?> GetAgentAsync(Guid id)
    {
        var matches = await _agentRepository.ListAsync(
            x => x.Id == id,
            null,
            x => x.AgentMcpToolServers,
            x => x.AgentSkillRelations,
            x => x.AgentAppRelations);
        return matches.FirstOrDefault();
    }

    public async Task<AgentModelRuntimeConfiguration?> GetModelRuntimeConfigurationAsync(Guid modelProviderId)
    {
        var modelProvider = await _modelProviderRepository.GetByIdAsync(modelProviderId);
        if (modelProvider == null)
        {
            return null;
        }

        var model = await _modelRepository.GetByIdAsync(modelProvider.ModelId);
        var provider = await _providerRepository.Queryable
            .Include(x => x.AuthConfigs)
            .SingleOrDefaultAsync(x => x.Id == modelProvider.ProviderId);
        return model == null || provider == null
            ? null
            : new AgentModelRuntimeConfiguration(modelProvider, model, provider);
    }

    public async Task<string[]> CollectNamedToolNamesAsync(Guid agentId, string? rawAgentTools)
    {
        var mergedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(rawAgentTools))
        {
            try
            {
                var directTools = JsonSerializer.Deserialize<string[]>(rawAgentTools) ?? [];
                foreach (var toolName in directTools.Where(static name => !string.IsNullOrWhiteSpace(name)))
                {
                    mergedNames.Add(toolName);
                }
            }
            catch (JsonException)
            {
            }
        }

        var appRelations = await _agentAppRelationRepository.ListAsync(x => x.AgentId == agentId);
        var appInstanceIds = appRelations.Select(x => x.AppInstanceId).Distinct().ToList();
        if (appInstanceIds.Count == 0)
        {
            return [.. mergedNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
        }

        var appInstances = await _appInstanceRepository.ListAsync(x => appInstanceIds.Contains(x.Id));
        var appNames = appInstances.Select(x => x.AppName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (appNames.Count == 0)
        {
            return [.. mergedNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
        }

        var appDefinitions = await _appDefinitionRepository.ListAsync(x => appNames.Contains(x.Name));
        foreach (var appInstance in appInstances)
        {
            var appDefinition = appDefinitions.FirstOrDefault(x =>
                string.Equals(x.Name, appInstance.AppName, StringComparison.OrdinalIgnoreCase));
            if (appDefinition == null)
            {
                continue;
            }

            foreach (var toolName in appDefinition.ToolNames.Where(static name => !string.IsNullOrWhiteSpace(name)))
            {
                mergedNames.Add(toolName);
            }
        }

        return [.. mergedNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<McpServer>> ListEnabledMcpToolServersByAgentAsync(Guid agentId)
    {
        var links = await _agentMcpToolServerRepository.ListAsync(x => x.AgentId == agentId);
        var serverIds = links.Select(x => x.McpToolServerId).Distinct().ToList();
        if (serverIds.Count == 0)
        {
            return [];
        }

        return await _mcpToolServerRepository.ListAsync(x => x.Enabled && serverIds.Contains(x.Id));
    }

    public async Task<IReadOnlyList<Skill>> ListSkillsByAgentAsync(Guid agentId)
    {
        var relations = await _agentSkillRelationRepository.ListAsync(x => x.AgentId == agentId);
        if (relations.Count == 0)
        {
            return [];
        }

        var skillIds = relations.Select(x => x.SkillId).Distinct().ToList();
        return await _skillRepository.ListAsync(x => skillIds.Contains(x.Id));
    }

    public async Task<Agent?> CreateAgentAsync(
        Agent agent,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? appInstanceIds,
        string user)
    {
        if (await HasInvalidModelProviderAsync(agent.ModelProviderId))
        {
            return null;
        }

        _agentDomainService.PrepareForCreate(agent, user);
        await _agentRepository.AddAsync(agent);
        await SyncAgentMcpToolServerRelationsAsync(agent.Id, mcpToolServerIds);
        await SyncAgentSkillRelationsAsync(agent.Id, skillIds);
        await SyncAgentAppRelationsAsync(agent.Id, appInstanceIds);
        await _unitOfWork.SaveChangesAsync();
        return agent;
    }

    public async Task<Agent?> UpdateAgentAsync(
        Guid id,
        Action<Agent> updateAction,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? appInstanceIds,
        string user)
    {
        var existing = await _agentRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        _agentDomainService.ApplyUpdate(existing, updateAction, user);
        if (await HasInvalidModelProviderAsync(existing.ModelProviderId))
        {
            return null;
        }

        _agentRepository.Update(existing);
        await SyncAgentMcpToolServerRelationsAsync(existing.Id, mcpToolServerIds);
        await SyncAgentSkillRelationsAsync(existing.Id, skillIds);
        await SyncAgentAppRelationsAsync(existing.Id, appInstanceIds);
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

        var appRelations = await _agentAppRelationRepository.ListAsync(x => x.AgentId == id);
        foreach (var relation in appRelations)
        {
            _agentAppRelationRepository.Remove(relation);
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
            await _agentMcpToolServerRepository.AddAsync(new AgentMcpServerRelation
            {
                AgentId = agentId,
                McpToolServerId = serverId
            });
        }
    }

    private async Task SyncAgentSkillRelationsAsync(Guid agentId, IEnumerable<Guid>? skillIds)
    {
        var existingLinks = await _agentSkillRelationRepository.ListAsync(x => x.AgentId == agentId);
        foreach (var link in existingLinks)
        {
            _agentSkillRelationRepository.Remove(link);
        }

        var requestedIds = (skillIds ?? [])
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (requestedIds.Count == 0)
        {
            return;
        }

        var existingSkills = await _skillRepository.ListAsync(x => requestedIds.Contains(x.Id));
        foreach (var skillId in existingSkills.Select(x => x.Id))
        {
            await _agentSkillRelationRepository.AddAsync(new AgentSkillRelation
            {
                AgentId = agentId,
                SkillId = skillId
            });
        }
    }

    private async Task SyncAgentAppRelationsAsync(Guid agentId, IEnumerable<Guid>? appInstanceIds)
    {
        var existingLinks = await _agentAppRelationRepository.ListAsync(x => x.AgentId == agentId);
        foreach (var link in existingLinks)
        {
            _agentAppRelationRepository.Remove(link);
        }

        var requestedIds = (appInstanceIds ?? [])
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (requestedIds.Count == 0)
        {
            return;
        }

        var appInstances = await _appInstanceRepository.ListAsync(x => requestedIds.Contains(x.Id));
        foreach (var appInstanceId in appInstances.Select(x => x.Id))
        {
            await _agentAppRelationRepository.AddAsync(new AgentAppRelation
            {
                AgentId = agentId,
                AppInstanceId = appInstanceId
            });
        }
    }
}
