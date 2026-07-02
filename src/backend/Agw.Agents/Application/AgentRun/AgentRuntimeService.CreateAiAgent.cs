using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;

using Microsoft.Agents.AI;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService
{
    public async Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAppService.GetAgentAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        return await CreateAiAgentAsync(new CreateAiAgentRequest
        {
            Agent = agent,
        }, cancellationToken);
    }

    public async Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        Guid? projectId,
        Guid? taskId,
        bool resume,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAppService.GetAgentAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        return await CreateAiAgentAsync(new CreateAiAgentRequest
        {
            Agent = agent,
            ProjectId = projectId,
            TaskId = taskId,
            Resume = resume,
        }, cancellationToken);
    }

    private async Task<AIAgent?> CreateAiAgentAsync(
        CreateAiAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Agent);
        Project? project = await _projectAppService
            .GetAsync(request.ProjectId ?? ProjectDefaults.DefaultBuiltInId);
        ArgumentNullException.ThrowIfNull(project);

        if (request.Agent.Type == AgentType.External)
        {
            TryCreateExternalAgent(request, project, out var externalAgent);
            return externalAgent;
        }

        return await CreateDefinitionAgentAsync(request.Agent, project, cancellationToken)
            .ConfigureAwait(false);
    }
}
