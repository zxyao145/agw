using System.Security.Claims;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

internal sealed class TestUserInfoService : IUserInfoService
{
    public TestUserInfoService(string userId = "tester")
    {
        UserId = userId;
    }

    public ClaimsPrincipal? Current { get; set; }

    public string? UserId { get; set; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(UserId);

    public string RequiredUserId =>
        !string.IsNullOrWhiteSpace(UserId) ? UserId! : throw new AgwException(ErrorCodes.AuthenticationRequired);
}
