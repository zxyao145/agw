using Agw.Auth.Contracts;
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
        using var userContext = UserInfoUtil.Push(context.User.Identity?.IsAuthenticated == true ? context.User : null);
        await _next(context);
    }
}
