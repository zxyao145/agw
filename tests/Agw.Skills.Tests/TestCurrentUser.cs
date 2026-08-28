using Agw.Shared.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Skills.Tests;

internal sealed class TestCurrentUser : ICurrentUser
{
    public TestCurrentUser(string userId)
    {
        UserId = userId;
    }

    public string? UserId { get; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(UserId);

    public string RequiredUserId =>
        !string.IsNullOrWhiteSpace(UserId) ? UserId : throw new AgwException(ErrorCodes.AuthenticationRequired);
}
