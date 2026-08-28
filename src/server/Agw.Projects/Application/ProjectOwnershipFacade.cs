using Agw.Projects.Contracts.Runtime;

namespace Agw.Projects.Application;

public sealed class ProjectOwnershipFacade : IProjectOwnershipFacade
{
    private readonly IProjectAppService _projectAppService;

    public ProjectOwnershipFacade(IProjectAppService projectAppService)
    {
        _projectAppService = projectAppService;
    }

    public async Task<IReadOnlySet<Guid>> ListOwnedProjectIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projects = await _projectAppService.ListForCurrentUserAsync().ConfigureAwait(false);
        return projects.Select(project => project.Id).ToHashSet();
    }

    public async Task<bool> IsOwnedAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _projectAppService.GetForCurrentUserAsync(projectId).ConfigureAwait(false) != null;
    }
}
