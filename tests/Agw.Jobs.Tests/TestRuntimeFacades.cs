using Agw.Agents.Contracts.Catalog;
using Agw.Projects.Contracts.Runtime;

namespace Agw.Jobs.Tests;

internal sealed class TestProjectRuntimeFacade : IProjectRuntimeFacade
{
    public Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(
        Guid projectId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult<ProjectRuntimeSnapshot?>(
            new ProjectRuntimeSnapshot(
                projectId,
                "project",
                null,
                null,
                [],
                new Dictionary<string, string>(),
                [],
                [],
                []
            )
        );

    public Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

internal sealed class TestAgentCatalogFacade : IAgentCatalogFacade
{
    public Task<IReadOnlyList<AgentDescriptor>> ListDiscoverableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentDescriptor>>([]);

    public Task<AgentDescriptor?> FindDiscoverableByNameAsync(
        string name,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<AgentDescriptor?>(null);

    public Task<IReadOnlySet<Guid>> FilterExistingMcpServerIdsAsync(
        IReadOnlyCollection<Guid> serverIds,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IReadOnlySet<Guid>>(serverIds.ToHashSet());

    public Task<bool> IsOwnedTargetAsync(
        AgentRuntimeType type,
        Guid id,
        string ownerUserId,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(true);

    public Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentCatalogMetrics(0, 0));
}
