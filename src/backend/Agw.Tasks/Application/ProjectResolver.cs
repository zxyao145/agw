using Agw.Shared;
using Agw.Shared.Abstractions.Repositories;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Application;

public class ProjectResolver
{
    private readonly IRepository<Project> _projectRepository;

    public ProjectResolver(IRepository<Project> projectRepository)
    {
        _projectRepository = projectRepository;
    }

    public Task<Project?> ResolveAsync(Guid? projectId, CancellationToken cancellationToken = default) =>
        ResolveInternalAsync(ProjectDefaults.GetDefaultProjectIdentifier(projectId), cancellationToken);

    public Task<Project?> ResolveRequiredAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        ResolveInternalAsync(projectId, cancellationToken);

    public async Task<Guid?> ResolveProjectIdAsync(Guid? projectId, CancellationToken cancellationToken = default)
    {
        var project = await ResolveAsync(projectId, cancellationToken);
        return project?.Id;
    }

    private async Task<Project?> ResolveInternalAsync(Guid? projectId, CancellationToken cancellationToken)
    {
        if (projectId == null)
        {
            return null;
        }

        return await _projectRepository.Queryable
                .FirstOrDefaultAsync(project => project.Id == projectId.Value, cancellationToken);
    }
}
