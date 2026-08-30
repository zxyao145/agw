using System.Security.Claims;
using Agw.Auth.Contracts;

namespace Agw.Auth.Application;

public sealed class UserInfoService : IUserInfoService
{
    public ClaimsPrincipal? Current
    {
        get => UserInfoUtil.Current;
        set => UserInfoUtil.Current = value;
    }

    public string? UserId => UserInfoUtil.UserId;

    public bool IsAuthenticated => UserInfoUtil.IsAuthenticated;

    public string RequiredUserId => UserInfoUtil.RequiredUserId;
}
