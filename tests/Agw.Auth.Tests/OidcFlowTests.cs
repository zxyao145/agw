using Agw.Auth.Application;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class OidcFlowTests
{
    [Fact]
    public void FailureRedirect_WebBaseUrlSet_TargetsWebOrigin()
    {
        var options = new OidcOptions
        {
            PublicBaseUrl = "http://127.0.0.1:30816",
            WebBaseUrl = "http://127.0.0.1:3001",
        };

        var redirect = OidcFlow.FailureRedirect(options, null, "provider-rejected");

        Assert.Equal("http://127.0.0.1:3001/login/?error=oidc-provider-rejected", redirect);
    }

    [Fact]
    public void FailureRedirect_WebBaseUrlUnset_FallsBackToPublicBaseUrl()
    {
        var options = new OidcOptions { PublicBaseUrl = "https://agw.example" };

        var redirect = OidcFlow.FailureRedirect(options, null, "provider-unavailable");

        Assert.Equal("https://agw.example/login/?error=oidc-provider-unavailable", redirect);
    }

    [Fact]
    public void WebOrigin_PrefersWebBaseUrlWhenSet()
    {
        Assert.Equal(
            "https://app.example",
            OidcFlow.WebOrigin(
                new OidcOptions { PublicBaseUrl = "https://agw.example", WebBaseUrl = "https://app.example" }
            )
        );
        Assert.Equal(
            "https://agw.example",
            OidcFlow.WebOrigin(new OidcOptions { PublicBaseUrl = "https://agw.example" })
        );
    }
}
