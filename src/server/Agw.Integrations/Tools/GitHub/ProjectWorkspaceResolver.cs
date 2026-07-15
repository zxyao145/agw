using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;

using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Tools.GitHub;

public sealed class ProjectWorkspaceResolver : IProjectWorkspaceResolver
{
    private readonly IRepository<Project> _projectRepository;

    public ProjectWorkspaceResolver(IRepository<Project> projectRepository)
    {
        _projectRepository = projectRepository;
    }

    public Task<string?> ResolveWorkspaceAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        return _projectRepository.Queryable
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => project.Workspace)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
