using Agw.Auth.Middleware;

using Microsoft.AspNetCore.Builder;

namespace Agw.Auth.Extensions;

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseAgwAuth(this IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseMiddleware<AgwAuthenticationMiddleware>();
        app.UseMiddleware<UserInfoContextMiddleware>();
        app.UseMiddleware<AgwAntiforgeryMiddleware>();
        app.UseMiddleware<AgwAuthorizationGuardMiddleware>();
        return app;
    }
}
