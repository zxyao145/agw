namespace Agw.Integrations.Contracts.OAuth;

public sealed class OAuthAuthorizeStartRequest
{
    public Guid ConnectionId { get; set; }
    public string ReturnPath { get; set; } = "/integrations";
    public OAuthCompletionTarget CompletionTarget { get; set; }
}

public sealed class OAuthAuthorizeStartResponse
{
    public string AuthorizationUrl { get; init; } = string.Empty;
}

public sealed class OAuthCallbackInfoResponse
{
    public string CallbackUrl { get; init; } = string.Empty;
}

public sealed class OAuthCallbackResult
{
    public required string RedirectPath { get; init; }
    public required bool Success { get; init; }
    public required OAuthCompletionTarget CompletionTarget { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OAuthCompletionTarget
{
    Web,
    Desktop
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
