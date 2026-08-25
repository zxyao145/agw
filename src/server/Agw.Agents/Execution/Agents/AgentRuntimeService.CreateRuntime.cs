using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Runtimes;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService
{
    /// <summary>
    /// 根据任务、Agent 配置和归一化 context 创建可恢复的 Agent 运行时。
    /// </summary>
    public Task<AgentRuntime?> CreateRuntimeAsync(
        Guid agentId,
        AgentExecutionTask task,
        SettingCommand settings,
        CancellationToken cancellationToken = default
    ) => CreateRuntimeCoreAsync(agentId, task, settings, deferHumanInteractions: false, cancellationToken);

    /// <summary>
    /// 创建会把人机交互 Tool 延迟为可 checkpoint approval 边界的 Agent runtime。
    /// </summary>
    internal Task<AgentRuntime?> CreateDurableRuntimeAsync(
        Guid agentId,
        AgentExecutionTask task,
        SettingCommand settings,
        CancellationToken cancellationToken = default
    ) => CreateRuntimeCoreAsync(agentId, task, settings, deferHumanInteractions: true, cancellationToken);

    /// <summary>
    /// 复用普通和 durable runtime 的创建流程，并由 deferHumanInteractions 控制 Tool 包装策略。
    /// </summary>
    private async Task<AgentRuntime?> CreateRuntimeCoreAsync(
        Guid agentId,
        AgentExecutionTask task,
        SettingCommand settings,
        bool deferHumanInteractions,
        CancellationToken cancellationToken
    )
    {
        var agent = await _agentAppService.GetAgentForCurrentUserAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        Guid projectId = task.ProjectId;
        var resolvedContextId = ContextIdUtil.ResolveContextId(task.ContextId);
        var conversationId =
            task.ProjectConversationId != Guid.Empty
                ? task.ProjectConversationId
                : await _sessionStateStore
                    .ResolveProjectConversationIdAsync(projectId, resolvedContextId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? Guid.Empty;
        var sessionScope = new AgentSessionStateScope(conversationId, projectId, resolvedContextId, agent.Id);
        var persistedProviderSessionId = await GetExternalProviderSessionIdAsync(
            agent,
            projectId,
            resolvedContextId,
            cancellationToken
        );
        var (providerSessionId, isResume) = ResolveExternalProviderSession(
            agent,
            persistedProviderSessionId,
            settings.Resume
        );

        var fs = await _fileSystemResolver.ResolveAsync(projectId, cancellationToken);
        var rootStat = await fs.StatAsync("", cancellationToken);
        if (rootStat == null)
        {
            await fs.CreateDirectoryAsync("", cancellationToken);
        }

        var aiAgent = await CreateAiAgentAsync(
            new CreateAiAgentRequest
            {
                Agent = agent,
                EnvironmentVariables = settings.EnvironmentVariables,
                ProviderSessionId = providerSessionId,
                ProjectId = projectId,
                ConversationId = conversationId,
                IsResume = isResume,
                DeferHumanInteractions = deferHumanInteractions,
                OnExternalSessionStartedAsync = CreateExternalSessionStartedCallback(agent, task, resolvedContextId),
            },
            cancellationToken
        );
        if (aiAgent == null)
        {
            return null;
        }

        try
        {
            var agentSession = await _sessionStateStore
                .GetOrCreateAsync(agent, aiAgent, sessionScope, cancellationToken)
                .ConfigureAwait(false);
            _providerSessionState.InitializeSessionState(
                agentSession,
                resolvedContextId,
                ProjectDefaults.GetDefaultProjectIdentifier(projectId)
            );
            var summaryModelProviderId = ResolveSummaryModelProviderId(agent);
            return new AgentRuntime(
                logger: _logger,
                aiAgent,
                agentSession,
                projectId: projectId,
                contextId: resolvedContextId,
                sessionStateScope: sessionScope,
                agentType: agent.Type,
                enableSummary: agent.EnableSummary && summaryModelProviderId.HasValue,
                summaryModelProviderId: summaryModelProviderId,
                summaryService: _summaryService,
                conversationHistoryWriter: _conversationHistoryWriter
            );
        }
        catch
        {
            await DisposeAgentWithoutThrowingAsync(aiAgent).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Guid?> GetExternalProviderSessionIdAsync(
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
            new ProjectProviderSessionReference(projectId, contextId, agent.Id, agent.Name),
            cancellationToken
        );
        if (providerSessionId == null)
        {
            return null;
        }

        return Guid.TryParse(providerSessionId, out var parsedProviderSessionId) ? parsedProviderSessionId : null;
    }

    internal static (Guid? ProviderSessionId, bool IsResume) ResolveExternalProviderSession(
        Agent agent,
        Guid? persistedProviderSessionId,
        bool requestedResume
    )
    {
        if (IsClaudeCodeExternalAgent(agent))
        {
            return (persistedProviderSessionId ?? Guid.NewGuid(), persistedProviderSessionId.HasValue);
        }

        if (IsCodexExternalAgent(agent))
        {
            return (persistedProviderSessionId, persistedProviderSessionId.HasValue);
        }

        return (null, requestedResume);
    }

    private Func<string, CancellationToken, ValueTask>? CreateExternalSessionStartedCallback(
        Agent agent,
        AgentExecutionTask task,
        string contextId
    )
    {
        if (!UsesProviderSessionBinding(agent))
        {
            return null;
        }

        return async (providerSessionId, _) =>
        {
            try
            {
                await _providerSessions.SaveProviderSessionIdAsync(
                    new ProjectProviderSessionReference(task.ProjectId, contextId, agent.Id, agent.Name),
                    providerSessionId,
                    _turnContextAccessor?.Current?.UserId ?? Constants.AdminUserId,
                    CancellationToken.None
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
