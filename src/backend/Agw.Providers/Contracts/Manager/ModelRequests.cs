using Agw.Shared.Enums;

namespace Agw.Manager.Api.Contracts;

public record ModelCreateRequest(string Name, string? Description, ModelType Type, int MaxTokens);

public record ModelUpdateRequest(string Name, string? Description, ModelType Type, int MaxTokens);
