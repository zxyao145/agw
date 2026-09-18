using System.Text.Json.Serialization;

namespace Agw.Auth.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthProviderType
{
    Oidc,
    OAuth2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OAuth2ClientAuthMethod
{
    Post,
    Basic,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OAuth2IdentitySource
{
    UserInfo,
    AccessToken,
}

public sealed record OidcProviderResponse(string Id, string DisplayName, AuthProviderType Type);

public sealed record VerifiedOidcIdentity(
    string ProviderId,
    string Issuer,
    string Subject,
    string? DisplayName,
    string? Email
);

public sealed record OidcUser(string UserId, string DisplayName, int SessionVersion, string? ProviderId);

public sealed record DesktopExchangeRequest(string Code, string CodeVerifier);

public sealed record DesktopExchangeResponse(
    string Token,
    Guid TokenId,
    string UserId,
    string DisplayName,
    string LoginProvider
);

public interface IOidcIdentityStore
{
    Task<OidcUser> ResolveAsync(VerifiedOidcIdentity identity, CancellationToken cancellationToken = default);
    Task<OidcUser?> ReadAsync(string userId, CancellationToken cancellationToken = default);
    Task<string> CreateDesktopGrantAsync(
        OidcUser user,
        string providerId,
        string codeChallenge,
        CancellationToken cancellationToken = default
    );
    Task<DesktopExchangeResponse> ExchangeAsync(
        string code,
        string verifier,
        CancellationToken cancellationToken = default
    );
    Task<int> DeleteExpiredGrantsAsync(CancellationToken cancellationToken = default);
}

public interface IOidcProviderAvailability
{
    bool IsEnabled(string providerId);
}
