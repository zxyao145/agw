using System.Diagnostics;
using Microsoft.Extensions.Primitives;

namespace Agw.Host.Middleware;

public sealed class TraceIdResponseHeaderMiddleware
{
    private const string TraceParentHeaderName = "traceparent";
    private const string TraceIdHeaderName = "x-trace-id";

    private readonly RequestDelegate _next;

    public TraceIdResponseHeaderMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(
            static state =>
            {
                var httpContext = (HttpContext)state;
                httpContext.Response.Headers[TraceIdHeaderName] = GetTraceId(httpContext);
                return Task.CompletedTask;
            },
            context
        );

        await _next(context);
    }

    private static string GetTraceId(HttpContext context)
    {
        var traceId = Activity.Current?.TraceId.ToString();
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            return traceId;
        }

        if (
            context.Request.Headers.TryGetValue(TraceParentHeaderName, out StringValues traceParent)
            && traceParent.Count > 0
            && ActivityContext.TryParse(traceParent[0], null, out var activityContext)
        )
        {
            return activityContext.TraceId.ToString();
        }

        return context.TraceIdentifier;
    }
}
