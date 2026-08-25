namespace Agw.Agents.Contracts.Catalog;

public sealed record AgentDescriptor(Guid Id, string Name, string DisplayName, string DiscoveryDescription);

public sealed record AgentCatalogMetrics(int AgentCount, int AgentflowCount);

public interface IAgentCatalogFacade
{
    Task<IReadOnlyList<AgentDescriptor>> ListDiscoverableAsync(CancellationToken cancellationToken = default);

    Task<AgentDescriptor?> FindDiscoverableByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<Guid>> FilterExistingMcpServerIdsAsync(
        IReadOnlyCollection<Guid> serverIds,
        CancellationToken cancellationToken = default
    );

    Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default);
}

public interface IAgentReferenceFacade
{
    Task<bool> UsesAnyModelProviderAsync(
        IReadOnlyCollection<Guid> modelProviderIds,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetAgentIdsBySkillIdsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    );

    Task RemoveSkillBindingsAsync(Guid skillId, CancellationToken cancellationToken = default);
}
