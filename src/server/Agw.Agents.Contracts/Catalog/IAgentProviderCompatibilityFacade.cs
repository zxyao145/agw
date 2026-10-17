using Agw.Providers.Contracts;

namespace Agw.Agents.Contracts.Catalog;

public interface IAgentProviderCompatibilityFacade
{
    Task EnsureCompatibleAsync(
        IReadOnlyCollection<Guid> modelProviderIds,
        ProviderType providerType,
        CancellationToken cancellationToken = default
    );
}
