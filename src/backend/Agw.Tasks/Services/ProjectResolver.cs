using Agw.Shared;
using Agw.Shared.Abstractions.Repositories;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Services;

public class ProjectResolver
{
    private readonly IRepository<Project> _projectRepository;

    public ProjectResolver(IRepository<Project> projectRepository)
    {
        _projectRepository = projectRepository;
    }

    public Task<Project?> ResolveAsync(string? projectId, CancellationToken cancellationToken = default) =>
        ResolveInternalAsync(ProjectDefaults.GetDefaultProjectIdentifier(projectId), cancellationToken);

    public Task<Project?> ResolveRequiredAsync(string projectId, CancellationToken cancellationToken = default) =>
        ResolveInternalAsync(projectId, cancellationToken);

    public async Task<Guid?> ResolveProjectIdAsync(string? projectId, CancellationToken cancellationToken = default)
    {
        var project = await ResolveAsync(projectId, cancellationToken);
        return project?.Id;
    }

    private async Task<Project?> ResolveInternalAsync(string? projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        var normalizedProjectId = projectId.Trim();
        if (Guid.TryParse(normalizedProjectId, out var projectGuid))
        {
            return await _projectRepository.Queryable
                .FirstOrDefaultAsync(project => project.Id == projectGuid, cancellationToken);
        }

        return await _projectRepository.Queryable
            .FirstOrDefaultAsync(
                project => project.Name.ToLower() == normalizedProjectId.ToLower(),
                cancellationToken);
    }
}
