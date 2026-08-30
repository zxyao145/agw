using Agw.Projects.Contracts.Runtime;

namespace Agw.Integrations.Tools.GitHub;

public sealed class ProjectWorkspaceResolver : IProjectWorkspaceResolver
{
    private readonly IProjectRuntimeFacade _projects;

    public ProjectWorkspaceResolver(IProjectRuntimeFacade projects)
    {
        _projects = projects;
    }

    public Task<string?> ResolveWorkspaceAsync(Guid projectId, CancellationToken cancellationToken)
    {
        return _projects.GetWorkspaceAsync(projectId, cancellationToken);
    }
}
