using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.ExternalAgents;
using Agw.Auth.Contracts;
using Agw.Providers.Contracts.References;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Pagination;
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
    private readonly IAgentsDbContext _dbContext;
    private readonly AgentDefinitionDomainService _definitionDomainService;
    private readonly AgentResourceBindingDomainService _resourceBindingDomainService;
    private readonly IAgentDeletionCoordinator _deletionCoordinator;
    private readonly IModelProviderReferenceFacade _modelProviderReferences;
    private readonly ISkillReferenceFacade _skillReferences;
    private readonly IUserInfoService _userInfoService;

    public AgentAppService(
        IAgentsDbContext dbContext,
        AgentDefinitionDomainService definitionDomainService,
        AgentResourceBindingDomainService resourceBindingDomainService,
        IModelProviderReferenceFacade modelProviderReferences,
        ISkillReferenceFacade skillReferences,
        IUserInfoService userInfoService,
        IAgentDeletionCoordinator deletionCoordinator
    )
    {
        _dbContext = dbContext;
        _definitionDomainService = definitionDomainService;
        _resourceBindingDomainService = resourceBindingDomainService;
        _modelProviderReferences = modelProviderReferences;
        _skillReferences = skillReferences;
        _userInfoService = userInfoService;
        _deletionCoordinator = deletionCoordinator;
    }

    public async Task<IReadOnlyList<Agent>> ListAgentsAsync()
    {
        var user = _userInfoService.RequiredUserId;
        var agents = await CreateAgentQuery(user).ToListAsync();
        await _resourceBindingDomainService.RetainVisibleRelationsAsync(agents).ConfigureAwait(false);
        return agents.OrderBy(x => x.Name).ThenByDescending(x => x.CreateTime).ToList();
    }

    public async Task<IReadOnlyList<Agent>> ListAgentsForCurrentUserAsync()
    {
        var user = _userInfoService.RequiredUserId;
        var agents = await CreateAgentQuery(user).Where(agent => agent.Enable).ToListAsync();
        await _resourceBindingDomainService.RetainVisibleRelationsAsync(agents).ConfigureAwait(false);
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
        await _resourceBindingDomainService.RetainVisibleRelationsAsync(page.Items).ConfigureAwait(false);
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
        await _resourceBindingDomainService.RetainVisibleRelationsAsync(page.Items).ConfigureAwait(false);
        return page;
    }

    public async Task<Agent?> GetAgentAsync(Guid id)
    {
        var user = _userInfoService.RequiredUserId;
        var agent = await CreateAgentQuery(user).FirstOrDefaultAsync(agent => agent.Id == id);
        if (agent != null)
        {
            await _resourceBindingDomainService.RetainVisibleRelationsAsync([agent]).ConfigureAwait(false);
        }
        return agent;
    }

    public async Task<Agent?> GetAgentForCurrentUserAsync(Guid id)
    {
        var user = _userInfoService.RequiredUserId;
        var agent = await CreateAgentQuery(user).FirstOrDefaultAsync(agent => agent.Id == id);
        if (agent != null)
        {
            await _resourceBindingDomainService.RetainVisibleRelationsAsync([agent]).ConfigureAwait(false);
        }
        return agent;
    }

    public async Task<AgentModelRuntimeConfiguration?> GetModelRuntimeConfigurationAsync(Guid modelProviderId)
    {
        var snapshot = await _modelProviderReferences.GetRuntimeSnapshotAsync(modelProviderId).ConfigureAwait(false);
        return snapshot == null ? null : new AgentModelRuntimeConfiguration(snapshot.Model, snapshot.Provider);
    }

    public async Task<AgentModelRuntimeConfiguration?> GetExternalModelRuntimeConfigurationAsync(
        EngineKind kind,
        Guid? modelProviderId
    )
    {
        if (!modelProviderId.HasValue)
        {
            return null;
        }

        var configuration =
            await GetModelRuntimeConfigurationAsync(modelProviderId.Value).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound, "Model provider is unavailable.");
        ExternalAgentDefaults.ValidateProviderType(kind, configuration.Provider.ProviderType);
        return configuration;
    }

    public async Task<IReadOnlyList<McpServer>> ListEnabledMcpToolServersAsync(IEnumerable<Guid>? mcpToolServerIds)
    {
        var serverIds = (mcpToolServerIds ?? []).Where(static id => id != Guid.Empty).Distinct().ToList();
        if (serverIds.Count == 0)
        {
            return [];
        }

        var user = _userInfoService.RequiredUserId;
        return await _dbContext
            .McpToolServers.AsNoTracking()
            .Where(x => x.Enabled && serverIds.Contains(x.Id) && x.CreateBy == user)
            .ToListAsync();
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
        _ = _userInfoService.RequiredUserId;
        if (!await _definitionDomainService.AreModelProvidersVisibleAsync(agent))
        {
            return null;
        }

        if (agent.Type == AgentType.External)
        {
            await _definitionDomainService.EnsureExternalModelProviderSupportedAsync(
                agent.ExternalAgentKind,
                agent.ModelProviderId
            );
            agent.Extra = AgentExtraSettings.Normalize(agent.Extra);
        }
        agent.ResponseSchema = AgentResponseSchema.Normalize(agent.ResponseSchema);
        new AgentBehavior(agent).PrepareForCreate();
        await _definitionDomainService.EnsureNameAvailableAsync(agent);
        if (agent.Type == AgentType.External && ExternalAgentDefaults.ShouldUseDefaultExtra(agent.Extra))
        {
            agent.Extra = ExternalAgentDefaults.GetDefaultExtra(agent.ExternalAgentKind);
        }
        await _dbContext.Agents.AddAsync(agent);
        await SyncAgentMcpToolServerRelationsAsync(agent.Id, mcpToolServerIds);
        await SyncAgentSkillRelationsAsync(agent.Id, skillIds);
        await SyncAgentConnectionRelationsAsync(agent.Id, connectionIds);
        await _dbContext.SaveChangesAsync();
        return agent;
    }

    public async Task<Agent?> UpdateAgentAsync(Guid id, AgentUpdateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = _userInfoService.RequiredUserId;

        var existing = await _dbContext.Agents.SingleOrDefaultAsync(agent => agent.Id == id && agent.CreateBy == user);
        if (existing == null)
        {
            return null;
        }

        var update = CreateUpdate(command);
        var behavior = new AgentBehavior(existing);
        behavior.EnsureUpdateAllowed(update);
        if (existing.Type == AgentType.External)
        {
            var modelProviderId = behavior.ResolveModelProviderId(update);
            if (!await _definitionDomainService.IsModelProviderVisibleAsync(modelProviderId))
            {
                return null;
            }
            await _definitionDomainService.EnsureExternalModelProviderSupportedAsync(
                existing.ExternalAgentKind,
                modelProviderId
            );
        }

        behavior.ApplyUpdate(update);
        if (!await _definitionDomainService.AreModelProvidersVisibleAsync(existing))
        {
            return null;
        }

        // Preserve audit stamping even when only bindings change or the update is a no-op.
        _dbContext.Agents.Entry(existing).Property(agent => agent.DisplayName).IsModified = true;
        if (behavior.UpdatesResourceBindings())
        {
            await SyncAgentMcpToolServerRelationsAsync(existing.Id, command.McpToolServerIds);
            await SyncAgentSkillRelationsAsync(existing.Id, command.SkillIds);
            if (command.IsSpecified(AgentUpdateField.ConnectionIds))
            {
                await SyncAgentConnectionRelationsAsync(existing.Id, command.ConnectionIds);
            }
        }

        await _dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task<Agent?> UpdateAgentEnabledAsync(
        Guid id,
        bool enable,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        var existing = await _dbContext.Agents.FirstOrDefaultAsync(
            agent => agent.Id == id && agent.CreateBy == ownerUserId,
            cancellationToken
        );
        if (existing == null)
        {
            return null;
        }

        existing.Enable = enable;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public Task<bool> DeleteAgentAsync(Guid id, CancellationToken cancellationToken = default) =>
        _deletionCoordinator.DeleteAsync(id, _userInfoService.RequiredUserId, cancellationToken);

    // 归一化已指定取值中的 JSON 后交给 AgentBehavior，由它按 Agent 类型校验并决定哪些字段写入实体。
    // Normalize the JSON in the specified values and hand them to AgentBehavior, which validates them per agent type and decides which fields reach the entity.
    private static AgentUpdate CreateUpdate(AgentUpdateCommand command) =>
        new()
        {
            SpecifiedFields = command.SpecifiedFields,
            DisplayName = command.DisplayName,
            Description = command.Description,
            SystemPrompt = command.SystemPrompt,
            ModelProviderId = command.ModelProviderId,
            Tools = command.Tools,
            Extra = command.IsSpecified(AgentUpdateField.Extra)
                ? AgentExtraSettings.Normalize(command.Extra)
                : command.Extra,
            EnvironmentVariables = command.EnvironmentVariables,
            EnableSummary = command.EnableSummary,
            SummaryModelProviderId = command.SummaryModelProviderId,
            ResponseSchema = command.IsSpecified(AgentUpdateField.ResponseSchema)
                ? AgentResponseSchema.Normalize(command.ResponseSchema)
                : command.ResponseSchema,
        };

    private async Task SyncAgentMcpToolServerRelationsAsync(Guid agentId, IEnumerable<Guid>? mcpToolServerIds)
    {
        var existingLinks = await _dbContext.AgentMcpToolServers.Where(link => link.AgentId == agentId).ToListAsync();
        var (addedIds, removedIds) = await _resourceBindingDomainService
            .PlanMcpToolServerBindingsAsync(
                existingLinks.Select(link => link.McpToolServerId).ToList(),
                mcpToolServerIds
            )
            .ConfigureAwait(false);

        foreach (var link in existingLinks.Where(link => removedIds.Contains(link.McpToolServerId)))
        {
            _dbContext.AgentMcpToolServers.Remove(link);
        }
        foreach (var serverId in addedIds)
        {
            await _dbContext.AgentMcpToolServers.AddAsync(
                new AgentMcpServerRelation { AgentId = agentId, McpToolServerId = serverId }
            );
        }
    }

    private async Task SyncAgentSkillRelationsAsync(Guid agentId, IEnumerable<Guid>? skillIds)
    {
        var existingLinks = await _dbContext.AgentSkillRelations.Where(link => link.AgentId == agentId).ToListAsync();
        var (addedIds, removedIds) = await _resourceBindingDomainService
            .PlanSkillBindingsAsync(existingLinks.Select(link => link.SkillId).ToList(), skillIds)
            .ConfigureAwait(false);

        foreach (var link in existingLinks.Where(link => removedIds.Contains(link.SkillId)))
        {
            _dbContext.AgentSkillRelations.Remove(link);
        }
        foreach (var skillId in addedIds)
        {
            await _dbContext.AgentSkillRelations.AddAsync(
                new AgentSkillRelation { AgentId = agentId, SkillId = skillId }
            );
        }
    }

    private IQueryable<Agent> CreateAgentQuery(string ownerUserId)
    {
        IQueryable<Agent> query = _dbContext
            .Agents.Include(agent =>
                agent.AgentMcpToolServers.Where(relation => relation.McpToolServer.CreateBy == ownerUserId)
            )
            .Include(agent => agent.AgentSkillRelations)
            .Include(agent => agent.AgentConnectionRelations)
            .Where(agent => agent.CreateBy == ownerUserId);
        return query.AsNoTracking().AsSplitQuery();
    }

    private async Task SyncAgentConnectionRelationsAsync(Guid agentId, IEnumerable<Guid>? connectionIds)
    {
        var existingLinks = await _dbContext
            .AgentConnectionRelations.Where(link => link.AgentId == agentId)
            .ToListAsync();
        var (addedIds, removedIds) = await _resourceBindingDomainService
            .PlanConnectionBindingsAsync(existingLinks.Select(link => link.ConnectionId).ToList(), connectionIds)
            .ConfigureAwait(false);

        foreach (var link in existingLinks.Where(link => removedIds.Contains(link.ConnectionId)))
        {
            _dbContext.AgentConnectionRelations.Remove(link);
        }
        foreach (var connectionId in addedIds)
        {
            await _dbContext.AgentConnectionRelations.AddAsync(
                new AgentConnectionRelation { AgentId = agentId, ConnectionId = connectionId }
            );
        }
    }
}
