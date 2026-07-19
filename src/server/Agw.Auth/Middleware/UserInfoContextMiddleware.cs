using Agw.Auth.Application;

using Microsoft.AspNetCore.Http;

namespace Agw.Auth.Middleware;

public sealed class UserInfoContextMiddleware
{
    private readonly RequestDelegate _next;

    public UserInfoContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IUserInfoService userInfoService)
    {
        var previous = userInfoService.Current;
        try
        {
            userInfoService.Current = context.User.Identity?.IsAuthenticated == true
                ? context.User
                : null;
            await _next(context);
        }
        finally
        {
            userInfoService.Current = previous;
        }
    }
}
