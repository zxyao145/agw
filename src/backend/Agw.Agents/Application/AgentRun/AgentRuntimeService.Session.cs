using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Contracts;
using Agw.Agents.ExternalAgents;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;

using Microsoft.Extensions.Logging;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService
{
    public async Task<AgentExecSession?> CreateSessionAsync(
        Guid agentId,
        ProjectTask task,
        SettingCommand settings,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAppService.GetAgentAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        Guid projectId = task.ProjectId;
        string taskIdString = task.Id.Normalize();
        var providerSessionId = await GetCodexProviderSessionIdAsync(agent, task.Id, cancellationToken);
        var resume = IsCodexExternalAgent(agent)
            ? providerSessionId.HasValue
            : settings.Resume;

        var resolvedContextId = string.IsNullOrWhiteSpace(task.ContextId)
            ? TaskUtil.GenContextId()
            : task.ContextId;

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
            TaskId = task.Id,
            ProviderSessionId = providerSessionId,
            ProjectId = projectId,
            Resume = resume,
            OnExternalSessionStartedAsync = CreateExternalSessionStartedCallback(agent, task),
        }, cancellationToken);
        if (aiAgent == null)
        {
            return null;
        }

        var agentSession = await GetOrCreateThreadAsync(agent, aiAgent, taskIdString, cancellationToken);
        _providerSessionState.InitializeSessionState(agentSession, resolvedContextId, taskIdString,
            ProjectDefaults.GetDefaultProjectIdentifier(projectId));
        return new AgentExecSession(
            aiAgent,
            agentSession,
            projectId: projectId,
            contextId: resolvedContextId,
            taskIdString,
            AgentRuntimeType.Agent,
            agentId,
            agent.Name,
            _logger,
            taskTitle: agent.Name);
    }

    private async Task<Guid?> GetCodexProviderSessionIdAsync(
        Agent agent,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        if (!IsCodexExternalAgent(agent))
        {
            return null;
        }

        var binding = await _projectTaskSessionBindingService.GetAsync(
            taskId,
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
        ProjectTask task)
    {
        if (!IsCodexExternalAgent(agent))
        {
            return null;
        }

        return async (providerSessionId, _) =>
        {
            try
            {
                await _projectTaskSessionBindingService.UpsertAsync(
                    task.Id,
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
                    "Failed to save provider session binding for task {TaskId}, agent {AgentId}.",
                    task.Id,
                    agent.Id);
            }
        };
    }
    
    
    private static bool IsCodexExternalAgent(Agent agent) =>
        agent.Type == AgentType.External
        && string.Equals(agent.Name, AgentNames.Codex, StringComparison.OrdinalIgnoreCase);
}
