using System.Security.Claims;

using Agw.Auth.Application;
using Agw.Auth.Security;
using Agw.Shared;

using Microsoft.AspNetCore.Http;

namespace Agw.Auth.Middleware;

/// <summary>
/// 为 HTTP 请求和受支持的 WebSocket 握手建立 Agw 用户身份，并校验 WebSocket 来源。
/// </summary>
public sealed class AgwAuthenticationMiddleware
{
    private const string ExecutionHubPath = "/api/hubs/exec";
    private const string SignalRAccessTokenQueryParameter = "access_token";

    private readonly RequestDelegate _next;
    private readonly bool _allowDevelopmentDesktopOrigin;

    /// <summary>
    /// 初始化 Agw 身份认证中间件。
    /// </summary>
    /// <param name="next">管道中的下一个请求委托。</param>
    /// <param name="allowDevelopmentDesktopOrigin">是否允许仅供开发环境使用的 Desktop Origin。</param>
    public AgwAuthenticationMiddleware(RequestDelegate next, bool allowDevelopmentDesktopOrigin)
    {
        _next = next;
        _allowDevelopmentDesktopOrigin = allowDevelopmentDesktopOrigin;
    }

    /// <summary>
    /// 验证 Bearer Token 或本机可信请求，并在允许时继续执行请求管道。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <param name="stateStore">保存管理员密码和会话版本的状态存储。</param>
    /// <param name="tokenStore">保存并验证 API Token 的数据库存储。</param>
    public async Task InvokeAsync(
        HttpContext context,
        IAuthenticationStateStore stateStore,
        IApiTokenStore tokenStore)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            var bearerToken = ResolveBearerToken(context);
            if (bearerToken != null
                && await tokenStore.ValidateTokenAsync(bearerToken, context.RequestAborted))
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
                                     && LocalTrustedRequest.IsDesktopOrigin(origin, _allowDevelopmentDesktopOrigin);
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

    /// <summary>
    /// 从 Authorization Header 解析 Bearer Token；浏览器 WebSocket 无法设置该 Header，
    /// 因此仅为 Execution Hub 的 WebSocket 握手接受 SignalR 标准查询参数。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <returns>解析出的 Token；请求未携带受支持的 Token 时返回 <see langword="null"/>。</returns>
    private static string? ResolveBearerToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        if (!context.WebSockets.IsWebSocketRequest
            || !context.Request.Path.Equals(ExecutionHubPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var queryTokens = context.Request.Query[SignalRAccessTokenQueryParameter];
        return queryTokens.Count == 1 && !string.IsNullOrWhiteSpace(queryTokens[0])
            ? queryTokens[0]!.Trim()
            : null;
    }

    /// <summary>
    /// 创建代表 Agw 管理员的认证主体。
    /// </summary>
    /// <param name="userId">管理员用户标识。</param>
    /// <param name="userName">管理员用户名。</param>
    /// <param name="authenticationType">建立身份所使用的认证方式。</param>
    /// <param name="sessionVersion">当前认证会话版本。</param>
    /// <returns>包含管理员身份声明的认证主体。</returns>
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
