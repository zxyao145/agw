using System.Security.Claims;
using Agw.Shared.Exceptions;

namespace Agw.Shared.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return Constants.AdminUserId;
        }

        var userId = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return string.IsNullOrWhiteSpace(userId)
            ? throw new AgwException(ErrorCodes.AuthenticationRequired, "A stable user id is required.")
            : userId.Trim();
    }
}
