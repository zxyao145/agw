namespace Agw.Providers.Contracts.Manager;

public record ModelCreateRequest(string Name, string? Description, int MaxTokens);

public record ModelUpdateRequest(string Name, string? Description, int MaxTokens);
