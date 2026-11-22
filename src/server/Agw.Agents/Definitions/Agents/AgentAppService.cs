using Agw.Agents.Application.A2A;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.ExternalAgents;
using Agw.Auth.Contracts;
using Agw.Providers.Contracts.References;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Coordination;
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
    private readonly IApplicationLock _applicationLock;
    private readonly IA2AAgentCardClient _cardClient;
    private readonly TimeProvider _timeProvider;

    public AgentAppService(
        IAgentsDbContext dbContext,
        AgentDefinitionDomainService definitionDomainService,
        AgentResourceBindingDomainService resourceBindingDomainService,
        IModelProviderReferenceFacade modelProviderReferences,
        ISkillReferenceFacade skillReferences,
        IUserInfoService userInfoService,
        IAgentDeletionCoordinator deletionCoordinator,
        IApplicationLock applicationLock,
        IA2AAgentCardClient cardClient,
        TimeProvider timeProvider
    )
    {
        _dbContext = dbContext;
        _definitionDomainService = definitionDomainService;
        _resourceBindingDomainService = resourceBindingDomainService;
        _modelProviderReferences = modelProviderReferences;
        _skillReferences = skillReferences;
        _userInfoService = userInfoService;
        _deletionCoordinator = deletionCoordinator;
        _applicationLock = applicationLock;
        _cardClient = cardClient;
        _timeProvider = timeProvider;
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

    public async Task<IReadOnlyList<SkillReferenceSnapshot>> ListSkillsAsync(
        IEnumerable<Guid>? skillIds,
        CancellationToken cancellationToken = default
    )
    {
        var requestedSkillIds = (skillIds ?? []).Where(static id => id != Guid.Empty).Distinct().ToList();
        if (requestedSkillIds.Count == 0)
        {
            return [];
        }

        return await _skillReferences
            .ResolveVisibleSkillsAsync(requestedSkillIds, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Agent?> CreateAgentAsync(
        Agent agent,
        AgentCreateCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var definitionOwner = _userInfoService.RequiredUserId;
        await using var mutationCancellation = await AgentDefinitionLock.AcquireScopeAsync(
            _applicationLock,
            definitionOwner,
            cancellationToken
        );
        cancellationToken = mutationCancellation.Token;
        var usesRemoteEndpoint = ExternalAgentRules.UsesA2AEndpoint(agent);
        var providersVisible =
            usesRemoteEndpoint
            || await _definitionDomainService.AreModelProvidersVisibleAsync(agent, cancellationToken);
        if (!providersVisible)
        {
            return null;
        }
        await PrepareCreateSettingsAsync(agent, usesRemoteEndpoint, cancellationToken);
        var behavior = new AgentBehavior(agent);
        behavior.PrepareForCreate(command.A2AEndpoint);
        await _definitionDomainService.EnsureNameAvailableAsync(agent);
        if (command.A2AEndpoint is { } input)
        {
            behavior.SetA2AEndpoint(await FetchEndpointAsync(input, null, cancellationToken));
        }
        else if (agent.Type == AgentType.External && AgentExtraSettings.OmitEmptyObject(agent.Extra) is null)
        {
            agent.Extra = ExternalAgentDefaults.GetDefaultExtra(agent.ExternalAgentKind);
        }
        await _dbContext.Agents.AddAsync(agent, cancellationToken);
        await SyncAgentMcpToolServerRelationsAsync(agent.Id, command.McpToolServerIds);
        await SyncAgentSkillRelationsAsync(agent.Id, command.SkillIds);
        await SyncAgentConnectionRelationsAsync(agent.Id, command.ConnectionIds);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return agent;
    }

    private async Task PrepareCreateSettingsAsync(
        Agent agent,
        bool usesRemoteEndpoint,
        CancellationToken cancellationToken
    )
    {
        if (usesRemoteEndpoint)
        {
            agent.Extra = AgentExtraSettings.OmitEmptyObject(agent.Extra);
            return;
        }
        if (agent.Type == AgentType.External)
        {
            await _definitionDomainService.EnsureExternalModelProviderSupportedAsync(
                agent.ExternalAgentKind,
                agent.ModelProviderId,
                cancellationToken
            );
            agent.Extra = AgentExtraSettings.Normalize(agent.Extra);
        }
        agent.ResponseSchema = AgentResponseSchema.Normalize(agent.ResponseSchema);
    }

    public async Task<Agent?> UpdateAgentAsync(
        Guid id,
        AgentUpdateCommand command,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = _userInfoService.RequiredUserId;
        await using var mutationCancellation = await AgentDefinitionLock.AcquireScopeAsync(
            _applicationLock,
            user,
            cancellationToken
        );
        cancellationToken = mutationCancellation.Token;

        var existing = await _dbContext
            .Agents.Include(agent => agent.A2AEndpoint)
            .SingleOrDefaultAsync(agent => agent.Id == id && agent.CreateBy == user, cancellationToken);
        if (existing == null)
        {
            return null;
        }

        var update = CreateUpdate(command, existing);
        var behavior = new AgentBehavior(existing);
        behavior.EnsureUpdateAllowed(update);
        if (!await CanUpdateModelProviderAsync(existing, behavior, update, cancellationToken))
        {
            return null;
        }

        var endpoint = behavior.ResolveA2AEndpointInput(update.A2AEndpoint) is { } input
            ? await FetchEndpointAsync(input, existing.A2AEndpoint, cancellationToken)
            : null;
        behavior.ApplyUpdate(update);
        if (!await _definitionDomainService.AreModelProvidersVisibleAsync(existing, cancellationToken))
        {
            return null;
        }

        if (endpoint is not null)
        {
            behavior.SetA2AEndpoint(endpoint);
        }

        // Preserve audit stamping even when only bindings change or the update is a no-op.
        _dbContext.Agents.Entry(existing).Property(agent => agent.DisplayName).IsModified = true;
        if (behavior.UpdatesResourceBindings())
        {
            await SyncUpdatedResourceBindingsAsync(existing.Id, command);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    private async Task<bool> CanUpdateModelProviderAsync(
        Agent agent,
        AgentBehavior behavior,
        AgentUpdate update,
        CancellationToken cancellationToken
    )
    {
        if (agent.Type != AgentType.External)
        {
            return true;
        }
        var modelProviderId = behavior.ResolveModelProviderId(update);
        if (!await _definitionDomainService.IsModelProviderVisibleAsync(modelProviderId, cancellationToken))
        {
            return false;
        }
        await _definitionDomainService.EnsureExternalModelProviderSupportedAsync(
            agent.ExternalAgentKind,
            modelProviderId,
            cancellationToken
        );
        return true;
    }

    private async Task SyncUpdatedResourceBindingsAsync(Guid agentId, AgentUpdateCommand command)
    {
        await SyncAgentMcpToolServerRelationsAsync(agentId, command.McpToolServerIds);
        await SyncAgentSkillRelationsAsync(agentId, command.SkillIds);
        if (command.IsSpecified(AgentUpdateField.ConnectionIds))
        {
            await SyncAgentConnectionRelationsAsync(agentId, command.ConnectionIds);
        }
    }

    public async Task<A2ACardPreviewResponse> PreviewA2ACardAsync(
        A2ACardPreviewRequest request,
        CancellationToken cancellationToken
    )
    {
        AgentA2AEndpoint? existing = null;
        if (request.AgentId is { } agentId)
        {
            var agent = await QueryA2AAgent(agentId, _userInfoService.RequiredUserId)
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
            existing = agent?.A2AEndpoint ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        var endpoint = await FetchEndpointAsync(request.ToInput(), existing, cancellationToken);
        return new A2ACardPreviewResponse
        {
            Card = endpoint.Extra.Card,
            InterfaceUrl = endpoint.Interface.Url,
            ProtocolBinding = endpoint.Interface.ProtocolBinding,
            Streaming = endpoint.Streaming,
        };
    }

    public async Task<Agent?> RefreshA2ACardAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var owner = _userInfoService.RequiredUserId;
        await using var mutation = await AgentDefinitionLock.AcquireScopeAsync(
            _applicationLock,
            owner,
            cancellationToken
        );
        cancellationToken = mutation.Token;
        var agent = await QueryA2AAgent(agentId, owner).SingleOrDefaultAsync(cancellationToken);
        if (agent?.A2AEndpoint is not { } existing)
        {
            return null;
        }

        var snapshot = await FetchEndpointAsync(new(existing.CardUrl, null), existing, cancellationToken);
        new AgentBehavior(agent).SetA2AEndpoint(snapshot);
        _dbContext.Agents.Entry(agent).Property(value => value.DisplayName).IsModified = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return agent;
    }

    private IQueryable<Agent> QueryA2AAgent(Guid id, string owner) =>
        _dbContext
            .Agents.Include(agent => agent.A2AEndpoint)
            .Where(agent =>
                agent.Id == id
                && agent.CreateBy == owner
                && agent.Type == AgentType.External
                && agent.ExternalAgentKind == EngineKind.A2A
            );

    private async Task<A2AEndpointSnapshot> FetchEndpointAsync(
        A2AEndpointInput input,
        AgentA2AEndpoint? existing,
        CancellationToken cancellationToken
    )
    {
        var url = A2AEndpointRules.ValidateCardUrl(input.CardUrl);
        var stored = existing is null ? null : A2AEndpointCredentialsJson.Deserialize(existing.Credentials);
        var credentials = A2AEndpointRules.ResolveCredentials(stored, input.Credentials);
        var card = await _cardClient.FetchAsync(url, credentials, cancellationToken);
        return new A2AEndpointSnapshot
        {
            CardUrl = input.CardUrl!,
            Credentials = A2AEndpointCredentialsJson.Serialize(credentials),
            Interface = card.Interface,
            Streaming = card.Streaming,
            Extra = new AgentA2AEndpointExtra { Card = card.Card, CardFetchedAt = _timeProvider.GetUtcNow() },
        };
    }

    public async Task<Agent?> UpdateAgentEnabledAsync(
        Guid id,
        bool enable,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        var existing = await _dbContext
            .Agents.Include(agent => agent.A2AEndpoint)
            .FirstOrDefaultAsync(agent => agent.Id == id && agent.CreateBy == ownerUserId, cancellationToken);
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

    // 归一化请求中的 JSON，再由 AgentBehavior 校验并决定写入实体的字段。
    // Normalize request JSON before AgentBehavior validates the update and decides which fields to persist.
    private static AgentUpdate CreateUpdate(AgentUpdateCommand command, Agent agent)
    {
        var usesRemoteEndpoint = ExternalAgentRules.UsesA2AEndpoint(agent);
        var extra = command.Extra;
        if (usesRemoteEndpoint)
        {
            extra = AgentExtraSettings.OmitEmptyObject(extra);
        }
        else if (command.IsSpecified(AgentUpdateField.Extra))
        {
            extra = AgentExtraSettings.Normalize(extra);
        }
        return new()
        {
            SpecifiedFields = command.SpecifiedFields,
            DisplayName = command.DisplayName,
            Description = command.Description,
            SystemPrompt = command.SystemPrompt,
            ModelProviderId = command.ModelProviderId,
            Tools = command.Tools,
            Extra = extra,
            EnvironmentVariables = command.EnvironmentVariables,
            EnableSummary = command.EnableSummary,
            SummaryModelProviderId = command.SummaryModelProviderId,
            ResponseSchema =
                !usesRemoteEndpoint && command.IsSpecified(AgentUpdateField.ResponseSchema)
                    ? AgentResponseSchema.Normalize(command.ResponseSchema)
                    : command.ResponseSchema,
            A2AEndpoint = command.A2AEndpoint,
        };
    }

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
            .Include(agent => agent.A2AEndpoint)
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
