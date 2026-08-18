using Agw.Files.Abstracts;
using Agw.Shared.Contracts.Projects;

namespace Agw.Projects.Application;

public sealed class ProjectFileSystemConfigurationProvider : IProjectFileSystemConfigurationProvider
{
    private readonly IProjectAppService _projectAppService;

    public ProjectFileSystemConfigurationProvider(IProjectAppService projectAppService)
    {
        _projectAppService = projectAppService;
    }

    public async Task<ProjectFileSystemConfiguration?> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = await _projectAppService.GetAsync(projectId);
        return project == null ? null : new ProjectFileSystemConfiguration(project.Name, project.Workspace);
    }
}
