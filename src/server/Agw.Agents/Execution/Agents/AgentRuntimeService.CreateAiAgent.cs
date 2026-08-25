using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Agents.Utils;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService
{
    public async Task<AIAgent?> CreateAiAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var agent = await _agentAppService.GetAgentForCurrentUserAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        return await CreateAiAgentAsync(new CreateAiAgentRequest { Agent = agent }, cancellationToken);
    }

    public async Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        Guid? projectId,
        bool resume,
        CancellationToken cancellationToken = default
    )
    {
        return await CreateAiAgentAsync(agentId, projectId, resume, environmentVariables: null, cancellationToken);
    }

    public async Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        Guid? projectId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default
    )
    {
        var agent = await _agentAppService.GetAgentForCurrentUserAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        return await CreateAiAgentAsync(
            new CreateAiAgentRequest
            {
                Agent = agent,
                EnvironmentVariables = environmentVariables,
                ProjectId = projectId,
                IsResume = resume,
            },
            cancellationToken
        );
    }

    public async Task<AIAgent?> CreateAgentflowNodeAgentAsync(
        Guid agentId,
        Guid? projectId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default
    )
    {
        var agent = await _agentAppService.GetAgentForCurrentUserAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        return await CreateAiAgentAsync(
            new CreateAiAgentRequest
            {
                Agent = agent,
                EnvironmentVariables = environmentVariables,
                ProjectId = projectId,
                IsResume = false,
                DefaultMode = "execute",
            },
            cancellationToken
        );
    }

    public async Task<AIAgent?> CreateAgentflowNodeAgentAsync(
        Guid agentId,
        Guid? projectId,
        Guid conversationId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default
    )
    {
        return await CreateAgentflowNodeAgentAsync(
            agentId,
            projectId,
            conversationId,
            environmentVariables,
            deferHumanInteractions: false,
            cancellationToken
        );
    }

    /// <summary>
    /// 创建指定 conversation 的 Agentflow node Agent，并选择是否延迟人机交互 Tool。
    /// </summary>
    public async Task<AIAgent?> CreateAgentflowNodeAgentAsync(
        Guid agentId,
        Guid? projectId,
        Guid conversationId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        bool deferHumanInteractions,
        CancellationToken cancellationToken = default
    )
    {
        var agent = await _agentAppService.GetAgentForCurrentUserAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        return await CreateAiAgentAsync(
            new CreateAiAgentRequest
            {
                Agent = agent,
                EnvironmentVariables = environmentVariables,
                ProjectId = projectId,
                ConversationId = conversationId,
                IsResume = false,
                DefaultMode = "execute",
                DeferHumanInteractions = deferHumanInteractions,
            },
            cancellationToken
        );
    }

    private async Task<AIAgent?> CreateAiAgentAsync(
        CreateAiAgentRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Agent);
        var projectSnapshot = await _projectRuntimeFacade.GetForCurrentUserAsync(
            request.ProjectId ?? ProjectDefaults.DefaultBuiltInId,
            cancellationToken
        );
        ArgumentNullException.ThrowIfNull(projectSnapshot);
        var project = MapProject(projectSnapshot);
        var environmentVariables = AgentRuntimeServiceUtil.MergeEnvironmentVariables(
            request.Agent.EnvironmentVariables,
            project.EnvironmentVariables,
            request.EnvironmentVariables
        );

        if (request.Agent.Type == AgentType.External)
        {
            TryCreateExternalAgent(request, project, environmentVariables, out var externalAgent);
            return externalAgent;
        }

        return await CreateDefinitionAgentAsync(
                request.Agent,
                project,
                request.ConversationId,
                environmentVariables,
                request.DefaultMode,
                cancellationToken,
                deferHumanInteractions: request.DeferHumanInteractions
            )
            .ConfigureAwait(false);
    }

    private static Project MapProject(ProjectRuntimeSnapshot project)
    {
        var mapped = new Project
        {
            Id = project.Id,
            Name = project.Name,
            Workspace = project.Workspace,
            ExtraSetting = project.ExtraSetting,
            Tools = project.Tools.ToList(),
            EnvironmentVariables = project.EnvironmentVariables.ToDictionary(),
        };
        mapped.ProjectSkillRelations = project
            .SkillIds.Select(skillId => new ProjectSkillRelation { ProjectId = mapped.Id, SkillId = skillId })
            .ToList();
        mapped.ProjectMcpToolServers = project
            .McpServerIds.Select(serverId => new ProjectMcpServerRelation
            {
                ProjectId = mapped.Id,
                McpToolServerId = serverId,
            })
            .ToList();
        mapped.ProjectConnectionRelations = project
            .ConnectionIds.Select(connectionId => new ProjectConnectionRelation
            {
                ProjectId = mapped.Id,
                ConnectionId = connectionId,
            })
            .ToList();
        return mapped;
    }
}
