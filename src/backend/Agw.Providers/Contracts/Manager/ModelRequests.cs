using Agw.Shared.Enums;

namespace Agw.Providers.Contracts.Manager;

public record ModelCreateRequest(string Name, string? Description, ModelType Type, int MaxTokens);

public record ModelUpdateRequest(string Name, string? Description, ModelType Type, int MaxTokens);
