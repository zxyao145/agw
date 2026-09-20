using Agw.Agents.Execution.Agents.Contracts;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Runtimes;
using Agw.Auth.Contracts;
using Agw.Shared.Runtime;

namespace Agw.Agents.Execution.Agents.Runtime;

public partial class AgentRuntimeService
{
    public async Task<bool> IsRuntimeCurrentAsync(AgentRuntime runtime, CancellationToken cancellationToken = default)
    {
        if (runtime.IsDisposed || runtime.ConfigurationVersion == null || runtime.SessionStateScope == null)
        {
            return false;
        }

        var version = await _configuration.ReadAsync(
            runtime.SessionStateScope.AgentId,
            runtime._projectId,
            cancellationToken
        );
        return version != null && version == runtime.ConfigurationVersion;
    }

    /// <summary>
    /// 根据任务、Agent 配置和归一化 context 创建可恢复的 Agent 运行时。
    /// </summary>
    public Task<AgentRuntime?> CreateRuntimeAsync(
        Guid agentId,
        AgentExecutionTask task,
        ExecutionSettings settings,
        CancellationToken cancellationToken = default
    ) => CreateRuntimeCoreAsync(agentId, task, settings, deferHumanInteractions: false, cancellationToken);

    /// <summary>
    /// 创建会把人机交互 Tool 延迟为可 checkpoint approval 边界的 Agent runtime。
    /// </summary>
    internal Task<AgentRuntime?> CreateDurableRuntimeAsync(
        Guid agentId,
        AgentExecutionTask task,
        ExecutionSettings settings,
        CancellationToken cancellationToken = default
    ) => CreateRuntimeCoreAsync(agentId, task, settings, deferHumanInteractions: true, cancellationToken);

    /// <summary>
    /// 复用普通和 durable runtime 的创建流程，并由 deferHumanInteractions 控制 Tool 包装策略。
    /// </summary>
    private async Task<AgentRuntime?> CreateRuntimeCoreAsync(
        Guid agentId,
        AgentExecutionTask task,
        ExecutionSettings settings,
        bool deferHumanInteractions,
        CancellationToken cancellationToken
    )
    {
        using var sessionContext = ConversationSessionContext.Push(task.ProjectId, task.ContextId, task.Generation);
        var agent = await _agentAppService.GetAgentForCurrentUserAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        var configurationVersion = await _configuration.ReadAsync(agentId, task.ProjectId, cancellationToken);
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
        var persistedProviderSessionId = await _providerBindings.GetExternalProviderSessionIdAsync(
            agent,
            projectId,
            resolvedContextId,
            cancellationToken
        );
        var (providerSessionId, isResume) = ExternalProviderSessionBindings.ResolveExternalProviderSession(
            agent,
            persistedProviderSessionId,
            settings.Resume
        );

        var workspace = ProjectWorkspaceContext.Get(projectId);
        var fs =
            workspace == null
                ? await _fileSystemResolver.ResolveAsync(projectId, cancellationToken)
                : await _fileSystemResolver.ResolveSnapshotAsync(projectId, workspace, null, cancellationToken);
        if (fs == null)
        {
            return null;
        }
        var rootStat = await fs.StatAsync("", cancellationToken);
        if (rootStat == null)
        {
            await fs.CreateDirectoryAsync("", cancellationToken);
        }

        var aiAgent = await CreateAiAgentAsync(
            new CreateAiAgentRequest
            {
                Agent = agent,
                PermissionMode = settings.PermissionMode,
                EnvironmentVariables = settings.EnvironmentVariables,
                ProviderSessionId = providerSessionId,
                ProjectId = projectId,
                ConversationId = conversationId,
                IsResume = isResume,
                DeferHumanInteractions = deferHumanInteractions,
                OnExternalSessionStartedAsync = _providerBindings.CreateExternalSessionStartedCallback(
                    agent,
                    task,
                    resolvedContextId,
                    ResolveExecutionUserId()
                ),
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
            MafSessionApprovalState.Apply(agentSession, settings.PermissionMode);
            var summaryModelProviderId = ResolveSummaryModelProviderId(agent);
            return new AgentRuntime(
                logger: _logger,
                aiAgent,
                agentSession,
                projectId: projectId,
                contextId: resolvedContextId,
                sessionStateScope: sessionScope,
                agentType: agent.Type,
                enableSummary: agent.EnableSummary,
                useStructuredResult: !string.IsNullOrWhiteSpace(agent.ResponseSchema),
                summaryModelProviderId: summaryModelProviderId,
                summaryService: _summaryService,
                conversationHistoryWriter: _conversationHistoryWriter
            )
            {
                ConfigurationVersion =
                    configurationVersion == await _configuration.ReadAsync(agentId, task.ProjectId, cancellationToken)
                        ? configurationVersion
                        : null,
            };
        }
        catch
        {
            await DisposeAgentWithoutThrowingAsync(aiAgent).ConfigureAwait(false);
            throw;
        }
    }

    private string ResolveExecutionUserId()
    {
        if (UserInfoUtil.IsContextActive)
        {
            return UserInfoUtil.RequiredUserId;
        }

        var userId = _turnContextAccessor?.Current?.UserId;
        return string.IsNullOrWhiteSpace(userId) ? Constants.AdminUserId : userId.Trim();
    }
}
