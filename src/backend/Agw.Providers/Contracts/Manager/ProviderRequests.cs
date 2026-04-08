using Agw.Providers.Domain.Entities;

namespace Agw.Providers.Contracts.Manager;

public record ProviderAuthConfigRequest(ProviderAuthType AuthType, string? ApiKey, string? EnvKey, bool Enable = true);

public record ProviderCreateRequest(
    string Name,
    ProviderType ProviderType,
    string? Description,
    string Endpoint,
    IReadOnlyList<ProviderAuthConfigRequest>? AuthConfigs);

public record ProviderUpdateRequest(
    string Name,
    ProviderType ProviderType,
    string? Description,
    string Endpoint,
    IReadOnlyList<ProviderAuthConfigRequest>? AuthConfigs);
