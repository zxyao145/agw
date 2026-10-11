using Agw.Agents.Execution.Agents.ExternalAgents;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Data.Entities.Agents;
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

    /// <summary>
    /// 读取绑定组当前生效的 provider session ID，作为新 Runtime 的持久化绑定快照；不使用 provider session 绑定的 Agent 返回 null。
    /// Reads the binding group's active provider session ID as the new Runtime's persisted binding snapshot; Agents without provider session bindings return null.
    /// </summary>
    public async Task<ExternalProviderSessionState?> ReadStateAsync(
        Agent agent,
        AgentExecutionTask task,
        string contextId,
        CancellationToken cancellationToken
    )
    {
        if (!UsesProviderSessionBinding(agent))
        {
            return null;
        }

        var reference = new ProjectProviderSessionReference(
            task.ProjectId,
            contextId,
            agent.Id,
            agent.Name,
            task.Generation
        );
        return new ExternalProviderSessionState(
            reference,
            await _providerSessions.GetProviderSessionIdAsync(reference, cancellationToken)
        );
    }

    /// <summary>
    /// 判断 Runtime 的持久化绑定快照是否仍是绑定组当前生效的 ID；绑定保存失败的 Runtime 不可复用，读取失败直接传播。
    /// Reports whether the Runtime's persisted binding snapshot is still the binding group's active ID; a Runtime whose binding save failed cannot be reused, and read failures propagate.
    /// </summary>
    public async Task<bool> IsSessionCurrentAsync(
        ExternalProviderSessionState state,
        CancellationToken cancellationToken
    )
    {
        if (state.HasFailed)
        {
            return false;
        }

        var current = await _providerSessions.GetProviderSessionIdAsync(state.Reference, cancellationToken);
        return string.Equals(state.PersistedProviderSessionId, current, StringComparison.Ordinal);
    }

    internal Guid? ParseProviderSessionId(Agent agent, ExternalProviderSessionState? state)
    {
        if (state?.PersistedProviderSessionId is not { } providerSessionId)
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
            state.Reference.ContextId
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

    /// <summary>
    /// 创建 SDK 的 session 通知回调：保存成功后更新持久化绑定快照；保存失败时记录首次异常、使 Runtime 不可复用并向上传播，之后的通知直接传播首次异常。
    /// Creates the SDK session notification callback: a successful save updates the persisted binding snapshot; a failed save records the first exception, makes the Runtime non-reusable and propagates, and later notifications propagate the first exception at once.
    /// </summary>
    public Func<string, CancellationToken, ValueTask>? CreateExternalSessionStartedCallback(
        ExternalProviderSessionState? state,
        string executionUserId
    )
    {
        if (state == null)
        {
            return null;
        }

        return async (providerSessionId, callbackCancellationToken) =>
        {
            state.ThrowIfFailed();
            try
            {
                state.MarkSaved(
                    await _providerSessions.SaveProviderSessionIdAsync(
                        state.Reference,
                        providerSessionId,
                        executionUserId,
                        callbackCancellationToken
                    )
                );
            }
            catch (Exception ex)
            {
                state.MarkFailed(ex);
                _logger.LogError(
                    ex,
                    "Failed to save the provider session binding for project {ProjectId}, context {ContextId}, external Agent {AgentName}/{AgentId}.",
                    state.Reference.ProjectId,
                    state.Reference.ContextId,
                    state.Reference.ExternalAgentName,
                    state.Reference.AgentId
                );
                throw;
            }
        };
    }
}
