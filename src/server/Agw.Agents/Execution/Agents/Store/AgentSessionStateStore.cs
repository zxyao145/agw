using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Auth.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Store;

public sealed class AgentSessionStateStore
{
    private readonly HybridCache? _cache;
    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly IApplicationLock? _applicationLock;
    private readonly ILogger<AgentSessionStateStore> _logger;
    private readonly TimeProvider _timeProvider;

    public AgentSessionStateStore(
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        ILogger<AgentSessionStateStore> logger
    )
        : this(serviceScopeFactory, timeProvider, logger, InMemoryApplicationLock.Shared) { }

    public AgentSessionStateStore(
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        ILogger<AgentSessionStateStore> logger,
        IApplicationLock applicationLock
    )
    {
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _applicationLock = applicationLock;
    }

    internal AgentSessionStateStore(HybridCache cache, ILogger<AgentSessionStateStore> logger)
    {
        _cache = cache;
        _timeProvider = TimeProvider.System;
        _logger = logger;
    }

    public async Task<AgentSession> GetOrCreateAsync(
        Agent agent,
        AIAgent aiAgent,
        AgentSessionStateScope sessionScope,
        CancellationToken cancellationToken
    )
    {
        return await GetOrCreateAsync(agent.Type, aiAgent, sessionScope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentSession> GetOrCreateForNodeAsync(
        AIAgent aiAgent,
        AgentSessionStateScope sessionScope,
        CancellationToken cancellationToken
    )
    {
        var agentType = await GetAgentTypeAsync(sessionScope.AgentId, cancellationToken).ConfigureAwait(false);
        return await GetOrCreateAsync(agentType, aiAgent, sessionScope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentSession> GetOrCreateAsync(
        AgentType? agentType,
        AIAgent aiAgent,
        AgentSessionStateScope sessionScope,
        CancellationToken cancellationToken
    )
    {
        if (agentType is null or AgentType.External)
        {
            return await aiAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        var serialized = await ReadAsync(sessionScope, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(serialized))
        {
            return await aiAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var serializedSession = JsonSerializer.Deserialize<JsonElement>(serialized);
            return await aiAgent
                .DeserializeSessionAsync(serializedSession, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Agent session state deserialization failed for project conversation {ProjectConversationId}, "
                    + "agent {AgentId}, and node {AgentflowNodeId}. A new session will be created.",
                sessionScope.ProjectConversationId,
                sessionScope.AgentId,
                sessionScope.AgentflowNodeId
            );
            return await aiAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveAsync(
        AgentType agentType,
        AgentSessionStateScope sessionScope,
        AIAgent aiAgent,
        AgentSession session,
        CancellationToken cancellationToken
    )
    {
        if (agentType == AgentType.External)
        {
            return;
        }

        if (!UserInfoUtil.IsContextActive)
        {
            return;
        }

        var ownerUserId = UserInfoUtil.RequiredUserId;
        var serializedSession = await aiAgent
            .SerializeSessionAsync(session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var serialized = JsonSerializer.Serialize(serializedSession);
        if (_serviceScopeFactory == null)
        {
            await _cache!
                .SetAsync(sessionScope.CacheKey, serialized, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await using var lifecycleLease = await _applicationLock!
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(sessionScope.ProjectId), cancellationToken)
            .ConfigureAwait(false);
        var projectConversationId = await ResolveProjectConversationIdAsync(sessionScope, cancellationToken)
            .ConfigureAwait(false);
        if (!projectConversationId.HasValue)
        {
            _logger.LogWarning(
                "Agent session state was not saved because project {ProjectId} context {ContextId} does not exist.",
                sessionScope.ProjectId,
                sessionScope.ContextId
            );
            return;
        }

        var agentflowNodeId = sessionScope.AgentflowNodeId ?? string.Empty;
        await using var mutationLease = await _applicationLock!
            .AcquireAsync(
                $"agent-session:{projectConversationId.Value:D}:{sessionScope.AgentId:D}:" + agentflowNodeId,
                cancellationToken
            )
            .ConfigureAwait(false);

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IAgentSessionStatePersistence>();
        await persistence
            .SaveAsync(
                sessionScope.ProjectId,
                projectConversationId.Value,
                sessionScope.AgentId,
                agentflowNodeId,
                serialized,
                ownerUserId,
                _timeProvider.GetUtcNow(),
                cancellationToken,
                sessionScope.Generation
            )
            .ConfigureAwait(false);
    }

    public async Task SaveForNodeAsync(
        AgentSessionStateScope sessionScope,
        AIAgent aiAgent,
        AgentSession session,
        CancellationToken cancellationToken
    )
    {
        var agentType = await GetAgentTypeAsync(sessionScope.AgentId, cancellationToken).ConfigureAwait(false);
        if (agentType.HasValue)
        {
            await SaveAsync(agentType.Value, sessionScope, aiAgent, session, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> ReadAsync(AgentSessionStateScope sessionScope, CancellationToken cancellationToken)
    {
        if (!UserInfoUtil.IsContextActive)
        {
            return string.Empty;
        }

        var ownerUserId = UserInfoUtil.RequiredUserId;
        if (_serviceScopeFactory == null)
        {
            return await _cache!
                .GetOrCreateAsync(
                    sessionScope.CacheKey,
                    _ => ValueTask.FromResult(string.Empty),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
        }

        var projectConversationId = await ResolveProjectConversationIdAsync(sessionScope, cancellationToken)
            .ConfigureAwait(false);
        if (!projectConversationId.HasValue)
        {
            return string.Empty;
        }

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IAgentSessionStatePersistence>();
        return await persistence
                .ReadAsync(
                    sessionScope.ProjectId,
                    projectConversationId.Value,
                    sessionScope.AgentId,
                    sessionScope.AgentflowNodeId,
                    ownerUserId,
                    cancellationToken,
                    sessionScope.Generation
                )
                .ConfigureAwait(false)
            ?? string.Empty;
    }

    internal async Task<Guid?> ResolveProjectConversationIdAsync(
        AgentSessionStateScope sessionScope,
        CancellationToken cancellationToken
    )
    {
        if (!UserInfoUtil.IsContextActive)
        {
            return null;
        }

        var ownerUserId = UserInfoUtil.RequiredUserId;
        if (sessionScope.ProjectConversationId != Guid.Empty)
        {
            if (_serviceScopeFactory == null)
            {
                return sessionScope.ProjectConversationId;
            }

            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var persistence = scope.ServiceProvider.GetRequiredService<IAgentSessionStatePersistence>();
            return await persistence
                .ResolveProjectConversationIdAsync(
                    sessionScope.ProjectId,
                    sessionScope.ContextId,
                    sessionScope.ProjectConversationId,
                    ownerUserId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        return await ResolveProjectConversationIdAsync(
                sessionScope.ProjectId,
                sessionScope.ContextId,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal async Task<Guid?> ResolveProjectConversationIdAsync(
        Guid projectId,
        string contextId,
        CancellationToken cancellationToken
    )
    {
        if (!UserInfoUtil.IsContextActive || _serviceScopeFactory == null)
        {
            return null;
        }

        var ownerUserId = UserInfoUtil.RequiredUserId;
        await using var scope = _serviceScopeFactory!.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IAgentSessionStatePersistence>();
        return await persistence
            .ResolveProjectConversationIdAsync(
                projectId,
                contextId,
                projectConversationId: null,
                ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<AgentType?> GetAgentTypeAsync(Guid agentId, CancellationToken cancellationToken)
    {
        if (!UserInfoUtil.IsContextActive)
        {
            return null;
        }

        var ownerUserId = UserInfoUtil.RequiredUserId;
        if (_serviceScopeFactory == null)
        {
            return AgentType.System;
        }

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IAgentSessionStatePersistence>();
        return await persistence.GetAgentTypeAsync(agentId, ownerUserId, cancellationToken).ConfigureAwait(false);
    }
}
