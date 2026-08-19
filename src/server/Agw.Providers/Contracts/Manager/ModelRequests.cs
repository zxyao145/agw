namespace Agw.Providers.Contracts.Manager;

public record ModelCreateRequest(string Name, string? Description, int MaxContextWindowTokens, int MaxOutputTokens);

public record ModelUpdateRequest(string Name, string? Description, int MaxContextWindowTokens, int MaxOutputTokens);
