using Agw.Auth.Security;
using Microsoft.AspNetCore.Http;

namespace Agw.Auth.Middleware;

/// <summary>
/// 校验 WebSocket 握手的来源；同源请求始终允许，跨源请求必须位于配置的可信来源列表中。
/// </summary>
public sealed class AgwWebSocketOriginMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IReadOnlyCollection<string> _allowedOrigins;

    /// <summary>
    /// 初始化 WebSocket 来源校验中间件。
    /// </summary>
    /// <param name="next">管道中的下一个请求委托。</param>
    /// <param name="allowedOrigins">配置的跨源可信 Origin 列表。</param>
    public AgwWebSocketOriginMiddleware(RequestDelegate next, IReadOnlyCollection<string> allowedOrigins)
    {
        _next = next;
        _allowedOrigins = allowedOrigins;
    }

    /// <summary>
    /// 校验 WebSocket Origin 后继续执行请求管道。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    public async Task InvokeAsync(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (
            context.WebSockets.IsWebSocketRequest
            && context.Request.Headers.ContainsKey("Origin")
            && !LocalTrustedRequest.IsSameOrigin(context)
            && !_allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
        )
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }
}
