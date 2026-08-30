using System.Security.Claims;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

internal sealed class TestUserInfoService : IUserInfoService
{
    private ClaimsPrincipal? _current;
    private string? _userId;

    public TestUserInfoService(string userId = "tester")
    {
        UserId = userId;
    }

    public ClaimsPrincipal? Current
    {
        get => _current;
        set
        {
            _current = value;
            _userId = value?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            UserInfoUtil.Current = value;
        }
    }

    public string? UserId
    {
        get => _userId;
        set => Current = CreatePrincipal(value);
    }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(UserId);

    public string RequiredUserId
    {
        get
        {
            if (string.IsNullOrWhiteSpace(UserId))
            {
                throw new AgwException(ErrorCodes.AuthenticationRequired);
            }

            UserInfoUtil.Current = Current;
            return UserId;
        }
    }

    private static ClaimsPrincipal? CreatePrincipal(string? userId) =>
        string.IsNullOrWhiteSpace(userId)
            ? null
            : new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType: "Test")
            );
}
