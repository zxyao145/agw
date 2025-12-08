namespace DSystem.Manager.Api.Contracts;

public record ProviderCreateRequest(string Name, string? Description, string Endpoint);

public record ProviderUpdateRequest(string Name, string? Description, string Endpoint);
