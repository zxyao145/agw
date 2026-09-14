using Agw.Files.Abstracts;
using Agw.Shared.Runtime;

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
        return project == null
            ? null
            : new ProjectFileSystemConfiguration(
                project.Name,
                project.Workspace,
                project.CreateBy,
                project
                    .AdditionalDirectories.Select(directory => new ProjectWorkspaceDirectory(
                        directory.Id,
                        directory.Path
                    ))
                    .ToArray()
            );
    }

    public Task<string?> GetOwnerUserIdAsync(Guid projectId, CancellationToken cancellationToken) =>
        _projectAppService.GetOwnerUserIdAsync(projectId, cancellationToken);
}
