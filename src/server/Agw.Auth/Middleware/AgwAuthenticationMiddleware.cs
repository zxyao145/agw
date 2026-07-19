using System.Security.Claims;

using Agw.Auth.Application;
using Agw.Auth.Security;
using Agw.Shared;

using Microsoft.AspNetCore.Http;

namespace Agw.Auth.Middleware;

public sealed class AgwAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public AgwAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IAuthenticationStateStore stateStore)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && stateStore.ValidateToken(authorization["Bearer ".Length..].Trim()))
            {
                context.User = CreatePrincipal(
                    Constants.AdminUserId,
                    Constants.AdminUserName,
                    AgwAuthDefaults.BearerScheme,
                    stateStore.GetAuthenticationSnapshot().SessionVersion);
            }
            else if (LocalTrustedRequest.IsLocalTrusted(context))
            {
                context.User = CreatePrincipal(
                    Constants.AdminUserId,
                    Constants.AdminUserName,
                    AgwAuthDefaults.LocalTrustedScheme,
                    stateStore.GetAuthenticationSnapshot().SessionVersion);
            }
        }

        var origin = context.Request.Headers.Origin.ToString();
        var isAuthenticatedDesktop = context.User.Identity?.AuthenticationType == AgwAuthDefaults.BearerScheme
                                     && LocalTrustedRequest.IsDesktopOrigin(origin);
        if (context.WebSockets.IsWebSocketRequest
            && context.Request.Headers.ContainsKey("Origin")
            && !LocalTrustedRequest.IsSameOrigin(context)
            && !isAuthenticatedDesktop)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }

    private static ClaimsPrincipal CreatePrincipal(string userId, string userName, string authenticationType,
        int sessionVersion)
    {
        var identity = new ClaimsIdentity(
            [
                // 用户Id
                new Claim(ClaimTypes.NameIdentifier, userId),
                // 用户名
                new Claim(ClaimTypes.Name, userName),
                new Claim(AgwAuthDefaults.SessionVersionClaimType, sessionVersion.ToString())
            ],
            authenticationType);
        return new ClaimsPrincipal(identity);
    }
}
