using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Contracts;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Extensions;

using Microsoft.Extensions.Logging;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService
{
    public async Task<AgentExecSession?> CreateSessionAsync(
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
        var resolvedContextId = ExecutionContextIdResolver.Resolve(task.ContextId);
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

        var agentSession = await _sessionStateStore.GetOrCreateAsync(agent, aiAgent, sessionKey, cancellationToken);
        _providerSessionState.InitializeSessionState(agentSession, resolvedContextId,
            ProjectDefaults.GetDefaultProjectIdentifier(projectId));
        return new AgentExecSession(
            logger: _logger,
            aiAgent,
            agentSession,
            projectId: projectId,
            contextId: resolvedContextId,
            sessionKey: sessionKey);
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
