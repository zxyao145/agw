using Agw.Shared.Contracts.Integrations;

namespace Agw.Integrations.Contracts.Manager;

public sealed record AppInstanceListItemResponse(
    Guid Id,
    string AppName,
    string DisplayName,
    string Provider,
    CategoryType? Category,
    bool UsePkce,
    string ClientId,
    bool HasClientSecret,
    bool IsAuthorized,
    bool IsAuthorizationExpired,
    DateTimeOffset? AuthorizationExpiresAtUtc,
    string? AuthorizationSubject,
    DateTimeOffset CreateTime,
    string? CreateBy,
    DateTimeOffset? UpdateTime,
    string? UpdateBy);
