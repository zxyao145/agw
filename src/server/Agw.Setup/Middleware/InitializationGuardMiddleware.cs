using Agw.Shared.Runtime;

using Microsoft.AspNetCore.Http;

namespace Agw.Setup.Middleware;

public class InitializationGuardMiddleware
{
    private static readonly PathString SetupPath = new("/setup");

    private readonly RequestDelegate _next;

    public InitializationGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IServerInitializationState initializationState)
    {
        var path = context.Request.Path;

        if (!initializationState.IsInitialized)
        {
            if (path.StartsWithSegments("/api/server-info")
                || path.StartsWithSegments("/api/health/live")
                || path.StartsWithSegments("/api/health/ready"))
            {
                await _next(context);
                return;
            }

            if (path.StartsWithSegments(SetupPath))
            {
                await _next(context);
                return;
            }

            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/openapi") || path.StartsWithSegments("/scalar"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { message = "System is not initialized yet. Complete /setup first." });
                return;
            }

            if (HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.Redirect("/setup");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // 初始化后，setup 页面将返回 404
        if (path.StartsWithSegments(SetupPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }
}
