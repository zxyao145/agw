using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Bens.Results;
using Microsoft.AspNetCore.Http;

namespace Agw.Auth.Middleware;

public sealed class AgwAuthorizationGuardMiddleware
{
    private static readonly string[] AnonymousApiPaths =
    [
        "/api/server-info",
        "/api/health/live",
        "/api/health/ready",
        "/api/auth/session",
        "/api/auth/antiforgery",
        "/api/auth/login",
        "/api/integrations/oauth/callback",
        "/api/integrations/oauth/desktop-complete",
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
        var isAnonymousPath =
            AnonymousApiPaths.Any(value => path.StartsWithSegments(value))
            || HttpMethods.IsGet(context.Request.Method)
                && (path.Equals("/api/auth/oidc/providers") || path.Equals("/api/auth/oidc/login"))
            || HttpMethods.IsPost(context.Request.Method) && path.Equals("/api/auth/desktop/exchange");

        if (isProtectedProtocol && !isAnonymousPath && !initializationState.IsInitialized)
        {
            var error = ErrorCodes.ServerNotInitialized;
            await ApiResult.Fail(error.Code, error.Message, (int)error.StatusCode).ExecuteAsync(context);
            return;
        }

        if (!isProtectedProtocol || isAnonymousPath || context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(
            new
            {
                code = 401_0003,
                title = "Authentication is required.",
                statusCode = StatusCodes.Status401Unauthorized,
            }
        );
    }
}
