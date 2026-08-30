using Agw.Projects.Contracts.Runtime;

namespace Agw.Projects.Application.Facades;

public sealed class ProjectRuntimeFacade : IProjectRuntimeFacade
{
    private readonly IProjectAppService _projectService;

    public ProjectRuntimeFacade(IProjectAppService projectService)
    {
        _projectService = projectService;
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

    public async Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = await _projectService.GetAsync(projectId).ConfigureAwait(false);
        return project?.Workspace;
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
            project.ProjectConnectionRelations.Select(relation => relation.ConnectionId).ToArray()
        );
}
