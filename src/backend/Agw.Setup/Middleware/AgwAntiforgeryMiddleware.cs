using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace Agw.Setup.Middleware;

public sealed class AgwAntiforgeryMiddleware
{
    private readonly RequestDelegate _next;

    public AgwAntiforgeryMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        var method = context.Request.Method;
        var requiresValidation = context.Request.Path.StartsWithSegments("/api")
            && method is "POST" or "PUT" or "PATCH" or "DELETE"
            && context.User.Identity?.AuthenticationType is "AgwCookie" or "LocalTrusted"
            && !context.WebSockets.IsWebSocketRequest;

        if (requiresValidation)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = 403_0003,
                    title = "Antiforgery validation failed.",
                    statusCode = StatusCodes.Status403Forbidden
                });
                return;
            }
        }

        await _next(context);
    }
}
