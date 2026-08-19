using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Contracts.Manager;

public record ProviderAuthConfigRequest(ProviderAuthType AuthType, string? ApiKey, string? EnvKey, bool Enable = true);

public record ProviderCreateRequest(
    string Name,
    ProviderType ProviderType,
    string? Description,
    string Endpoint,
    IReadOnlyList<ProviderAuthConfigRequest>? AuthConfigs,
    IReadOnlyList<string>? ModelNames = null
);

public record ProviderUpdateRequest(
    string Name,
    ProviderType ProviderType,
    string? Description,
    string Endpoint,
    IReadOnlyList<ProviderAuthConfigRequest>? AuthConfigs,
    IReadOnlyList<string>? ModelNames = null
);

public record ProviderModelDiscoveryRequest(ProviderType ProviderType, string Endpoint, string ApiKey);

public record ProviderModelDiscoveryResponse(IReadOnlyList<string> ModelNames);
