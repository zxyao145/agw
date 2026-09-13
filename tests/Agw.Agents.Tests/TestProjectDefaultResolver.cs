using Agw.Projects.Contracts.Runtime;

namespace Agw.Agents.Tests;

internal sealed class TestProjectDefaultResolver : IProjectDefaultResolver
{
    public Task<Guid?> ResolveDefaultProjectIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(ProjectDefaults.DefaultBuiltInId);

    public Task<Guid?> ResolveA2AProjectIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(ProjectDefaults.A2AId);
}
