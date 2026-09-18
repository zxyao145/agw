using Agw.Auth.Security;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;

namespace Agw.Auth.Application;

public static class OidcFlow
{
    public const string ClientKey = "agw:client";
    public const string ClientStateKey = "agw:client_state";
    public const string ChallengeKey = "agw:desktop_challenge";
    public const string StartedKey = "agw:callback_started";
    public const string ExplicitCookie = "agw.explicit-auth";
    public const string ExplicitHeader = "X-Agw-Explicit-Auth";
    public const string DesktopRedirect = "agw-desktop://auth/complete";

    public static string? Value(AuthenticationProperties? properties, string key) =>
        properties != null && properties.Items.TryGetValue(key, out var value) ? value : null;

    public static string ReturnUrl(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "/dashboard/";
        // Reject encoded separators too: clients and proxies may otherwise normalize them differently.
        var decoded = Uri.UnescapeDataString(value);
        if (
            value.Length > 2048
            || !value.StartsWith('/')
            || value.StartsWith("//")
            || decoded.StartsWith("//")
            || decoded.Contains((char)92)
            || decoded.Any(char.IsControl)
            || value.Contains((char)92)
            || value.Any(char.IsControl)
            || value.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase)
            || !Uri.IsWellFormedUriString(value, UriKind.Relative)
        )
            throw new AgwException(ErrorCodes.OAuthReturnPathInvalid);
        return value;
    }

    public static AuthenticationProperties Properties(
        string client,
        string? returnUrl,
        string? clientState,
        string? challenge,
        string? method
    )
    {
        if (client is not ("web" or "desktop"))
            throw new AgwException(ErrorCodes.InvalidParam);
        if (
            client == "desktop"
            && (
                !string.IsNullOrEmpty(returnUrl)
                || !DesktopLoginProof.IsChallenge(clientState)
                || !DesktopLoginProof.IsChallenge(challenge)
                || method != "S256"
            )
        )
            throw new AgwException(ErrorCodes.InvalidParam);
        var properties = new AuthenticationProperties { RedirectUri = client == "web" ? ReturnUrl(returnUrl) : "/" };
        properties.Items[ClientKey] = client;
        if (client == "desktop")
        {
            properties.Items[ClientStateKey] = clientState;
            properties.Items[ChallengeKey] = challenge;
        }
        return properties;
    }

    public static string FailureRedirect(OidcOptions options, AuthenticationProperties? properties, string error)
    {
        if (
            Value(properties, ClientKey) == "desktop"
            && Value(properties, ClientStateKey) is { } state
            && DesktopLoginProof.IsChallenge(state)
        )
            return QueryHelpers.AddQueryString(
                DesktopRedirect,
                new Dictionary<string, string?> { ["error"] = error, ["state"] = state }
            );
        return QueryHelpers.AddQueryString(WebOrigin(options) + "/login/", "error", "oidc-" + error);
    }

    // Browser-facing origin: the configured web origin, falling back to the callback origin so
    // single-origin deployments keep working without setting WebBaseUrl.
    public static string WebOrigin(OidcOptions options) =>
        string.IsNullOrEmpty(options.WebBaseUrl) ? options.PublicBaseUrl : options.WebBaseUrl;
}
