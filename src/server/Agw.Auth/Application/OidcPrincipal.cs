using System.Globalization;
using System.Security.Claims;
using Agw.Auth.Contracts;

namespace Agw.Auth.Application;

public static class OidcPrincipal
{
    public const string ProviderClaim = "agw_login_provider";
    public const string TokenIdClaim = "agw_token_id";
    public const string AuthenticationRejected = "agw_authentication_rejected";

    public static ClaimsPrincipal Create(OidcUser user, string? providerId = null)
    {
        return new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.UserId),
                    new Claim(ClaimTypes.Name, user.DisplayName),
                    new Claim(
                        AgwAuthDefaults.SessionVersionClaimType,
                        user.SessionVersion.ToString(CultureInfo.InvariantCulture)
                    ),
                    new Claim(ProviderClaim, providerId ?? user.ProviderId ?? string.Empty),
                ],
                AgwAuthDefaults.CookieScheme
            )
        );
    }
}
