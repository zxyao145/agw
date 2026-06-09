using Agw.Setup.Services;

using Microsoft.AspNetCore.Http;

namespace Agw.Setup.Middleware;

public class ApiKeyGuardMiddleware
{
    private const string ApiKeyHeader = "X-API-Key";

    private readonly RequestDelegate _next;

    public ApiKeyGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IInitializationStateStore stateStore)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var snapshot = stateStore.GetSnapshot();
        if (!snapshot.IsInitialized || string.IsNullOrWhiteSpace(snapshot.ApiKey))
        {
            await _next(context);
            return;
        }

        var requestApiKey = context.Request.Headers[ApiKeyHeader].ToString();
        if (string.IsNullOrEmpty(requestApiKey) && context.WebSockets.IsWebSocketRequest)
        {
            requestApiKey = context.Request.Query[ApiKeyHeader].ToString();
        }

        if (string.Equals(requestApiKey, snapshot.ApiKey, StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { message = $"Missing or invalid {ApiKeyHeader} header." });
    }
}
