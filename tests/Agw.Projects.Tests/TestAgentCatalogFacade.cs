using Agw.Agents.Contracts.Catalog;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Repositories;

namespace Agw.Projects.Tests;

internal sealed class TestAgentCatalogFacade : IAgentCatalogFacade
{
    private readonly IRepository<McpServer> _servers;

    public TestAgentCatalogFacade(IRepository<McpServer> servers)
    {
        _servers = servers;
    }

    public Task<IReadOnlyList<AgentDescriptor>> ListDiscoverableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentDescriptor>>([]);

    public Task<AgentDescriptor?> FindDiscoverableByNameAsync(
        string name,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<AgentDescriptor?>(null);

    public async Task<IReadOnlySet<Guid>> FilterExistingMcpServerIdsAsync(
        IReadOnlyCollection<Guid> serverIds,
        CancellationToken cancellationToken = default
    )
    {
        var ids = serverIds.ToHashSet();
        return (await _servers.ListAsync(server => ids.Contains(server.Id))).Select(server => server.Id).ToHashSet();
    }

    public Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentCatalogMetrics(0, 0));

    public Task<bool> IsOwnedTargetAsync(
        Agw.Agents.Contracts.Execution.AgentRuntimeType type,
        Guid id,
        string ownerUserId,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(true);
}
