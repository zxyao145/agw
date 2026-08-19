using Agw.Providers.Contracts.Manager;

namespace Agw.Providers.Application;

public interface IProviderModelDiscoveryService
{
    Task<ProviderModelDiscoveryResponse> DiscoverAsync(
        ProviderModelDiscoveryRequest request,
        CancellationToken cancellationToken = default
    );
}
