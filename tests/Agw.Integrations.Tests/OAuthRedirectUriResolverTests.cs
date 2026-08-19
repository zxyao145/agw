using Agw.Integrations.Application.OAuth;
using Microsoft.Extensions.Options;

namespace Agw.Integrations.Tests;

public sealed class OAuthRedirectUriResolverTests
{
    [Fact]
    public void ResolveCallbackUri_ConfiguredPublicBaseUrl_UsesApiOrigin()
    {
        var resolver = CreateResolver(publicBaseUrl: "https://api.agw.test/base", webBaseUrl: "https://app.agw.test");

        var result = resolver.ResolveCallbackUri("https://proxy.agw.test/");

        Assert.Equal("https://api.agw.test/base/api/integrations/oauth/callback", result);
    }

    [Fact]
    public void ResolveWebRedirectUri_ConfiguredWebBaseUrl_ReturnsIntegrationsPage()
    {
        var resolver = CreateResolver(publicBaseUrl: "https://api.agw.test", webBaseUrl: "https://app.agw.test");

        var result = resolver.ResolveWebRedirectUri("https://api.agw.test/", "/integrations?oauth=authorized");

        Assert.Equal("https://app.agw.test/integrations?oauth=authorized", result);
    }

    [Fact]
    public void ResolveUris_NoConfiguration_FallsBackToRequestOrigin()
    {
        var resolver = CreateResolver(publicBaseUrl: null, webBaseUrl: null);

        Assert.Equal(
            "https://agw.test/api/integrations/oauth/callback",
            resolver.ResolveCallbackUri("https://agw.test/")
        );
        Assert.Equal(
            "https://agw.test/integrations?oauth=authorized",
            resolver.ResolveWebRedirectUri("https://agw.test/", "/integrations?oauth=authorized")
        );
    }

    private static OAuthRedirectUriResolver CreateResolver(string? publicBaseUrl, string? webBaseUrl)
    {
        return new OAuthRedirectUriResolver(
            Options.Create(new OAuthRedirectOptions { PublicBaseUrl = publicBaseUrl, WebBaseUrl = webBaseUrl })
        );
    }
}
