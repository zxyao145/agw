using Agw.Agents.Execution.Agents.ExternalAgents;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Runtime;

public sealed class ExternalProviderSessionBindings
{
    private readonly IProjectProviderSessionFacade _providerSessions;
    private readonly ILogger<ExternalProviderSessionBindings> _logger;

    public ExternalProviderSessionBindings(
        IProjectProviderSessionFacade providerSessions,
        ILogger<ExternalProviderSessionBindings> logger
    )
    {
        _providerSessions = providerSessions;
        _logger = logger;
    }

    internal static bool UsesProviderSessionBinding(Agent agent) => EngineKinds.Resolve(agent) is not EngineKind.Maf;

    public async Task<Guid?> GetExternalProviderSessionIdAsync(
        Agent agent,
        Guid projectId,
        string contextId,
        CancellationToken cancellationToken
    )
    {
        if (!UsesProviderSessionBinding(agent))
        {
            return null;
        }

        var providerSessionId = await _providerSessions.GetProviderSessionIdAsync(
            new ProjectProviderSessionReference(
                projectId,
                contextId,
                agent.Id,
                agent.Name,
                ExecutionContextSlot.FindBound(projectId, contextId)?.Generation ?? 0
            ),
            cancellationToken
        );
        if (providerSessionId == null)
        {
            return null;
        }

        // Pi 0.84.4 emits UUIDv7 Session IDs, although the persisted provider binding is a string. If Pi adopts a
        // non-Guid format, preserve the raw value through the runtime instead of treating the binding as invalid.
        if (Guid.TryParse(providerSessionId, out var parsedProviderSessionId))
        {
            return parsedProviderSessionId;
        }

        _logger.LogWarning(
            "Ignoring an invalid provider session binding for external Agent {AgentName}/{AgentId} in context {ContextId}.",
            agent.Name,
            agent.Id,
            contextId
        );
        return null;
    }

    internal static (Guid? ProviderSessionId, bool IsResume) ResolveExternalProviderSession(
        Agent agent,
        Guid? persistedProviderSessionId,
        bool requestedResume
    )
    {
        var kind = EngineKinds.Resolve(agent);
        if (kind == EngineKind.ClaudeCode)
        {
            return (persistedProviderSessionId ?? Guid.NewGuid(), persistedProviderSessionId.HasValue);
        }

        if (kind is EngineKind.Codex or EngineKind.Pi)
        {
            return (persistedProviderSessionId, persistedProviderSessionId.HasValue);
        }

        return (null, requestedResume);
    }

    public Func<string, CancellationToken, ValueTask>? CreateExternalSessionStartedCallback(
        Agent agent,
        AgentExecutionTask task,
        string contextId,
        string executionUserId
    )
    {
        if (!UsesProviderSessionBinding(agent))
        {
            return null;
        }

        return async (providerSessionId, callbackCancellationToken) =>
        {
            try
            {
                await _providerSessions.SaveProviderSessionIdAsync(
                    new ProjectProviderSessionReference(
                        task.ProjectId,
                        contextId,
                        agent.Id,
                        agent.Name,
                        task.Generation
                    ),
                    providerSessionId,
                    executionUserId,
                    callbackCancellationToken
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to save provider session binding for context {ContextId}, agent {AgentId}.",
                    contextId,
                    agent.Id
                );
            }
        };
    }
}
