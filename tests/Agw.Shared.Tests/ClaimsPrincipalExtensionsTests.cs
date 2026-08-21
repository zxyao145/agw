using System.Security.Claims;
using Agw.Shared.Extensions;

namespace Agw.Shared.Tests;

public sealed class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void GetUserId_NameAndNameIdentifierDiffer_ReturnsTrimmedNameIdentifier()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, Constants.AdminUserName), new Claim(ClaimTypes.NameIdentifier, " 42 ")],
                "test"
            )
        );

        Assert.Equal("42", principal.GetUserId());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetUserId_NameIdentifierMissingOrBlank_ReturnsAdminUserId(string? userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, Constants.AdminUserName) };
        if (userId != null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        Assert.Equal(Constants.AdminUserId, principal.GetUserId());
    }

    [Fact]
    public void GetUserId_PrincipalMissing_ReturnsAdminUserId()
    {
        ClaimsPrincipal? principal = null;

        Assert.Equal(Constants.AdminUserId, principal.GetUserId());
    }
}
