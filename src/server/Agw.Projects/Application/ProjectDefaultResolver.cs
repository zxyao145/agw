using Agw.Projects.Contracts.Runtime;

namespace Agw.Projects.Application;

public sealed class ProjectDefaultResolver : IProjectDefaultResolver
{
    private readonly ProjectResolver _projectResolver;

    public ProjectDefaultResolver(ProjectResolver projectResolver)
    {
        _projectResolver = projectResolver;
    }

    public async Task<Guid?> ResolveDefaultProjectIdAsync(CancellationToken cancellationToken = default)
    {
        var project = await _projectResolver.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        return project?.Id;
    }

    public async Task<Guid?> ResolveA2AProjectIdAsync(CancellationToken cancellationToken = default)
    {
        var project = await _projectResolver.ResolveA2AAsync(cancellationToken).ConfigureAwait(false);
        return project?.Id;
    }
}
