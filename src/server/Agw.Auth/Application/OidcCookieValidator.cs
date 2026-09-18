using System.Globalization;
using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Shared;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Auth.Application;

public static class OidcCookieValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var id = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var version = context.Principal?.FindFirst(AgwAuthDefaults.SessionVersionClaimType)?.Value;
        var provider = context.Principal?.FindFirst(OidcPrincipal.ProviderClaim)?.Value;
        string? expected = null;
        if (id == Constants.AdminUserId && string.IsNullOrEmpty(provider))
        {
            var snapshot = context
                .HttpContext.RequestServices.GetRequiredService<IAuthenticationStateReader>()
                .GetAuthenticationSnapshot();
            if (snapshot.SessionVersion > 0)
                expected = snapshot.SessionVersion.ToString(CultureInfo.InvariantCulture);
        }
        else if (!string.IsNullOrWhiteSpace(provider) && id != null)
        {
            var store = context.HttpContext.RequestServices.GetRequiredService<IOidcIdentityStore>();
            var user = await store.ReadAsync(id, context.HttpContext.RequestAborted);
            if (user != null && user.ProviderId != null)
                expected = user.SessionVersion.ToString(CultureInfo.InvariantCulture);
        }

        if (expected == null || !string.Equals(version, expected, StringComparison.Ordinal))
        {
            context.HttpContext.Items[OidcPrincipal.AuthenticationRejected] = true;
            context.RejectPrincipal();
        }
    }
}
