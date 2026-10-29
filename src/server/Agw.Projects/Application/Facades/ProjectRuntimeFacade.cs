using Agw.Files.Abstracts;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;

namespace Agw.Projects.Application.Facades;

public sealed class ProjectRuntimeFacade : IProjectRuntimeFacade
{
    private readonly IProjectAppService _projectService;
    private readonly IProjectWorkspaceAccess _workspaceAccess;

    public ProjectRuntimeFacade(IProjectAppService projectService, IProjectWorkspaceAccess workspaceAccess)
    {
        _projectService = projectService;
        _workspaceAccess = workspaceAccess;
    }

    public async Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(
        Guid projectId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = await _projectService.GetForCurrentUserAsync(projectId).ConfigureAwait(false);
        return project == null ? null : Map(project);
    }

    /// <summary>
    /// <para>返回当前用户可见项目的有效主目录规范化绝对路径，返回前创建缺失的目录。</para>
    /// <para>Returns the normalized absolute effective primary directory of a Project visible to the current user,
    /// creating the directory when it is missing.</para>
    /// </summary>
    public async Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = await _projectService.GetAsync(projectId).ConfigureAwait(false);
        if (project == null)
            return null;

        var workspace = ProjectWorkspacePaths.CreateSnapshot(project.Id, project.Workspace).Workspace;
        await _workspaceAccess
            .ValidateAsync([workspace], creatingProjectId: null, cancellationToken)
            .ConfigureAwait(false);
        return ProjectWorkspacePaths.EnsureCreated(workspace);
    }

    private static ProjectRuntimeSnapshot Map(Agw.Shared.Data.Entities.Projects.Project project) =>
        new(
            project.Id,
            project.Name,
            project.Workspace,
            project.ExtraSetting,
            project.Tools,
            project.EnvironmentVariables,
            project.ProjectSkillRelations.Select(relation => relation.SkillId).ToArray(),
            project.ProjectMcpToolServers.Select(relation => relation.McpToolServerId).ToArray(),
            project.ProjectConnectionRelations.Select(relation => relation.ConnectionId).ToArray(),
            project
                .AdditionalDirectories.Select(directory => new ProjectWorkspaceDirectory(directory.Id, directory.Path))
                .ToArray()
        );
}
