using Agw.Agents.Execution.Agents.Contracts;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Runtimes;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Runtime;

namespace Agw.Agents.Execution.Agents.Runtime;

public partial class AgentRuntimeFactory
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
    /// 根据任务、Agent 配置和归一化 context 创建可恢复的 Agent 运行时；System Agent 的输入工具都包装为原生审批边界。
    /// Creates a resumable Agent runtime from the task, the Agent configuration and the normalized context; every input tool of a System Agent is wrapped as a native approval boundary.
    /// </summary>
    public async Task<AgentRuntime?> CreateRuntimeAsync(
        Guid agentId,
        AgentExecutionTask task,
        ExecutionSettings settings,
        CancellationToken cancellationToken = default
    )
    {
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

        var workspace = ExecutionContextSlot.GetWorkspaceSnapshot(projectId);
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
                DeferHumanInteractions = true,
                OnExternalSessionStartedAsync = _providerBindings.CreateExternalSessionStartedCallback(
                    agent,
                    task,
                    resolvedContextId,
                    UserInfoUtil.RequiredUserId
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

    /// <summary>
    /// 摘要模型未配置时，System Agent 使用自己的模型。
    /// A System Agent uses its own model when no summary model is configured.
    /// </summary>
    internal static Guid? ResolveSummaryModelProviderId(Agent agent) =>
        agent.SummaryModelProviderId ?? (agent.Type == AgentType.System ? agent.ModelProviderId : null);
}
