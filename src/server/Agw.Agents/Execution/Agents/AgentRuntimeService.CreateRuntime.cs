using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Contracts;
using Agw.Agents.Execution.Runtimes;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;

using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService
{
    /// <summary>
    /// 根据任务、Agent 配置和归一化 context 创建可恢复的 Agent 运行时。
    /// </summary>
    public async Task<AgentRuntime?> CreateRuntimeAsync(
        Guid agentId,
        TaskProjection task,
        SettingCommand settings,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAppService.GetAgentAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        Guid projectId = task.ProjectId;
        var resolvedContextId = ContextIdUtil.ResolveContextId(task.ContextId);
        var sessionKey = CreateSessionKey(projectId, resolvedContextId);
        var providerSessionId =
            await GetCodexProviderSessionIdAsync(agent, projectId, resolvedContextId, cancellationToken);
        var resume = IsCodexExternalAgent(agent)
            ? providerSessionId.HasValue
            : settings.Resume;

        var fs = await _fileSystemResolver.ResolveAsync(projectId, cancellationToken);
        var rootStat = await fs.StatAsync("", cancellationToken);
        if (rootStat == null)
        {
            await fs.CreateDirectoryAsync("", cancellationToken);
        }

        var aiAgent = await CreateAiAgentAsync(new CreateAiAgentRequest
        {
            Agent = agent,
            EnvironmentVariables = settings.EnvironmentVariables,
            ProviderSessionId = providerSessionId,
            ProjectId = projectId,
            Resume = resume,
            OnExternalSessionStartedAsync = CreateExternalSessionStartedCallback(agent, task, resolvedContextId),
        }, cancellationToken);
        if (aiAgent == null)
        {
            return null;
        }

        try
        {
            var agentSession = await _sessionStateStore
                .GetOrCreateAsync(agent, aiAgent, sessionKey, cancellationToken)
                .ConfigureAwait(false);
            _providerSessionState.InitializeSessionState(
                agentSession,
                resolvedContextId,
                ProjectDefaults.GetDefaultProjectIdentifier(projectId));
            var summaryModelProviderId = ResolveSummaryModelProviderId(agent);
            return new AgentRuntime(
                logger: _logger,
                aiAgent,
                agentSession,
                projectId: projectId,
                contextId: resolvedContextId,
                sessionKey: sessionKey,
                enableSummary: agent.EnableSummary && summaryModelProviderId.HasValue,
                summaryModelProviderId: summaryModelProviderId,
                summaryService: _summaryService);
        }
        catch
        {
            await DisposeAgentWithoutThrowingAsync(aiAgent).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Guid?> GetCodexProviderSessionIdAsync(
        Agent agent,
        Guid projectId,
        string contextId,
        CancellationToken cancellationToken)
    {
        if (!IsCodexExternalAgent(agent))
        {
            return null;
        }

        var binding = await _taskSessionBindingService.GetAsync(
            projectId,
            contextId,
            agent.Id,
            agent.Name,
            cancellationToken);
        if (binding == null)
        {
            return null;
        }

        return Guid.TryParse(binding.ProviderSessionId, out var providerSessionId)
            ? providerSessionId
            : null;
    }

    private Func<string, CancellationToken, ValueTask>? CreateExternalSessionStartedCallback(
        Agent agent,
        TaskProjection task,
        string contextId)
    {
        if (!IsCodexExternalAgent(agent))
        {
            return null;
        }

        return async (providerSessionId, _) =>
        {
            try
            {
                await _taskSessionBindingService.UpsertAsync(
                    task.ProjectId,
                    contextId,
                    agent.Id,
                    agent.Name,
                    providerSessionId,
                    "system",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to save provider session binding for context {ContextId}, agent {AgentId}.",
                    contextId,
                    agent.Id);
            }
        };
    }

    private static string CreateSessionKey(Guid projectId, string contextId) =>
        $"{ProjectDefaults.GetDefaultProjectIdentifier(projectId).Normalize()}:{contextId.Trim()}";
}
