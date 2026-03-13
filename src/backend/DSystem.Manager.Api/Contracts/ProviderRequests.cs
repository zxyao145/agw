using DSystem.Domain.Entities;

namespace DSystem.Manager.Api.Contracts;

public record ProviderCreateRequest(string Name, ProviderType ProviderType, string? Description, string Endpoint);

public record ProviderUpdateRequest(string Name, ProviderType ProviderType, string? Description, string Endpoint);
