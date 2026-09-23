using Agw.Agents.Execution.Agents.Contracts;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents.Runtime;

public partial class AgentRuntimeService
{
    /// <summary>
    /// 创建指定 conversation 的 Agentflow node Agent，并选择是否延迟人机交互 Tool。
    /// Create the Agentflow node Agent for one conversation, optionally deferring human interaction Tools.
    /// </summary>
    public async Task<AIAgent?> CreateAgentflowNodeAgentAsync(
        Guid agentId,
        Guid? projectId,
        Guid conversationId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        bool deferHumanInteractions,
        CancellationToken cancellationToken = default,
        AgwPermissionMode? permissionMode = null
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
                PermissionMode = permissionMode,
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
        var projectId = await ResolveProjectIdAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (!projectId.HasValue)
        {
            return null;
        }

        var projectSnapshot = await _projectRuntimeFacade.GetForCurrentUserAsync(projectId.Value, cancellationToken);
        if (projectSnapshot == null)
        {
            return null;
        }
        var project = MapProject(projectSnapshot);
        var workspace =
            ProjectWorkspaceContext.Get(project.Id)
            ?? ProjectWorkspacePaths.CreateSnapshot(
                project.Id,
                project.Workspace,
                project.AdditionalDirectories.Select(directory => new ProjectWorkspaceDirectory(
                    directory.Id,
                    directory.Path
                ))
            );
        project.Workspace = workspace.Workspace;
        project.AdditionalDirectories = workspace
            .AdditionalDirectories.Select(directory => new ProjectDirectory
            {
                Id = directory.Id,
                Path = directory.Path,
            })
            .ToList();
        var environmentVariables = AgentRuntimeServiceUtil.MergeEnvironmentVariables(
            request.Agent.EnvironmentVariables,
            project.EnvironmentVariables,
            request.EnvironmentVariables
        );

        if (request.Agent.Type == AgentType.External)
        {
            return await CreateExternalAgentAsync(request, project, environmentVariables, cancellationToken)
                .ConfigureAwait(false);
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
            AdditionalDirectories =
                project
                    .AdditionalDirectories?.Select(directory => new ProjectDirectory
                    {
                        Id = directory.Id,
                        Path = directory.Path,
                    })
                    .ToList()
                ?? [],
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
