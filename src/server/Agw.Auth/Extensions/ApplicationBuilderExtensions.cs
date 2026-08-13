using Agw.Auth.Middleware;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agw.Auth.Extensions;

/// <summary>
/// 提供 Agw 身份认证与授权中间件的应用管道注册方法。
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// 注册 WebSocket Feature、身份认证、用户上下文、防伪校验和 API 授权保护。
    /// WebSocket Feature 必须先建立，后续认证中间件才能识别握手并验证 SignalR 查询 Token。
    /// </summary>
    /// <param name="app">待配置的应用管道。</param>
    /// <returns>同一个应用管道构建器。</returns>
    public static IApplicationBuilder UseAgwAuth(this IApplicationBuilder app)
    {
        var allowDevelopmentDesktopOrigin = app.ApplicationServices
            .GetRequiredService<IHostEnvironment>()
            .IsDevelopment();

        app.UseWebSockets();
        app.UseAuthentication();
        app.UseMiddleware<AgwAuthenticationMiddleware>(allowDevelopmentDesktopOrigin);
        app.UseMiddleware<UserInfoContextMiddleware>();
        app.UseMiddleware<AgwAntiforgeryMiddleware>();
        app.UseMiddleware<AgwAuthorizationGuardMiddleware>();
        return app;
    }
}
