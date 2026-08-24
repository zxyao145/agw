using System.Security.Claims;
using Agw.Shared;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;

namespace Agw.Auth.Contracts;

public static class UserInfoUtil
{
    private static readonly AsyncLocal<ClaimsPrincipal?> CurrentUser = new();

    public static ClaimsPrincipal? Current
    {
        get => CurrentUser.Value;
        set => CurrentUser.Value = value;
    }

    public static string? UserId => Current?.GetUserId();

    public static bool IsAuthenticated => Current?.Identity?.IsAuthenticated == true;

    public static string RequiredUserId
    {
        get
        {
            if (!IsAuthenticated)
                throw new AgwException(ErrorCodes.AuthenticationRequired);
            return string.IsNullOrWhiteSpace(UserId) ? Constants.AdminUserId : UserId;
        }
    }
}
