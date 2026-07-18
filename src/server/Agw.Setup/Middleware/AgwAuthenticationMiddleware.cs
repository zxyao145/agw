using System.Security.Claims;

using Agw.Setup.Services;

using Microsoft.AspNetCore.Http;

namespace Agw.Setup.Middleware;

public sealed class AgwAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public AgwAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IInitializationStateStore stateStore)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && stateStore.ValidateToken(authorization["Bearer ".Length..].Trim()))
            {
                context.User = CreatePrincipal("Bearer", stateStore.GetSnapshot().SessionVersion);
            }
            else if (LocalTrustedRequest.IsLocalTrusted(context))
            {
                context.User = CreatePrincipal("LocalTrusted", stateStore.GetSnapshot().SessionVersion);
            }
        }

        var origin = context.Request.Headers.Origin.ToString();
        var isAuthenticatedDesktop = context.User.Identity?.AuthenticationType == "Bearer"
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

    private static ClaimsPrincipal CreatePrincipal(string authenticationType, int sessionVersion)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim("session_version", sessionVersion.ToString())],
            authenticationType);
        return new ClaimsPrincipal(identity);
    }
}
