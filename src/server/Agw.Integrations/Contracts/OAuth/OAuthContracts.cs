namespace Agw.Integrations.Contracts.OAuth;

public sealed class OAuthAuthorizeStartRequest
{
    public Guid ConnectionId { get; set; }
    public string ReturnPath { get; set; } = "/integrations";
}

public sealed class OAuthAuthorizeStartResponse
{
    public string AuthorizationUrl { get; init; } = string.Empty;
}

public sealed class OAuthCallbackResult
{
    public required string RedirectPath { get; init; }
    public required bool Success { get; init; }
}

public sealed class OAuthRefreshResponse
{
    public required Guid ConnectionId { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}

public sealed class OAuthRefreshRequest
{
    public Guid ConnectionId { get; set; }
}
