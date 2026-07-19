using System.Security.Claims;

namespace Agw.Auth.Application;

public interface IUserInfoService
{
    ClaimsPrincipal? Current { get; set; }

    string? UserId { get; }

    bool IsAuthenticated { get; }

    string RequiredUserId { get; }
}
