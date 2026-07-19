using Agw.Shared.Runtime;

using Microsoft.AspNetCore.Http;

namespace Agw.Auth.Middleware;

public sealed class AgwAuthorizationGuardMiddleware
{
    private static readonly string[] AnonymousApiPaths =
    [
        "/api/server-info",
        "/api/auth/session",
        "/api/auth/antiforgery",
        "/api/auth/login",
        "/api/integrations/oauth/callback"
    ];

    private readonly RequestDelegate _next;

    public AgwAuthorizationGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IServerInitializationState initializationState)
    {
        var path = context.Request.Path;
        var isProtectedProtocol = path.StartsWithSegments("/api") || path.StartsWithSegments("/a2a");
        var isAnonymousPath = AnonymousApiPaths.Any(value => path.StartsWithSegments(value));

        if (!isProtectedProtocol || isAnonymousPath || !initializationState.IsInitialized
            || context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            code = 401_0003,
            title = "Authentication is required.",
            statusCode = StatusCodes.Status401Unauthorized
        });
    }
}
