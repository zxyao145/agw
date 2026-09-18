using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class OidcOptionsTests
{
    [Fact]
    public void Load_MissingType_DefaultsToOidc()
    {
        var options = OidcOptions.Load(
            Configuration(
                ("Auth:Oidc:PublicBaseUrl", "https://agw.example"),
                ("Auth:Oidc:Providers:company:Enabled", "true"),
                ("Auth:Oidc:Providers:company:Authority", "https://idp.example"),
                ("Auth:Oidc:Providers:company:ClientId", "client"),
                ("Auth:Oidc:Providers:company:ClientSecret", "secret")
            ),
            new TestEnvironment()
        );

        Assert.Equal(AuthProviderType.Oidc, options.Providers["company"].Type);
    }

    [Fact]
    public void Load_InvalidWebBaseUrl_Throws()
    {
        var exception = Assert.Throws<AgwException>(() =>
            OidcOptions.Load(
                Configuration(
                    ("Auth:Oidc:PublicBaseUrl", "https://agw.example"),
                    ("Auth:Oidc:WebBaseUrl", "not-a-url"),
                    ("Auth:Oidc:Providers:company:Enabled", "true"),
                    ("Auth:Oidc:Providers:company:Authority", "https://idp.example"),
                    ("Auth:Oidc:Providers:company:ClientId", "client"),
                    ("Auth:Oidc:Providers:company:ClientSecret", "secret")
                ),
                new TestEnvironment()
            )
        );

        Assert.Contains("WebBaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_LoopbackWebBaseUrlInDevelopment_IsAccepted()
    {
        var options = OidcOptions.Load(
            Configuration(
                ("Auth:Oidc:PublicBaseUrl", "http://127.0.0.1:30816"),
                ("Auth:Oidc:WebBaseUrl", "http://127.0.0.1:3001"),
                ("Auth:Oidc:Providers:company:Enabled", "true"),
                ("Auth:Oidc:Providers:company:Authority", "https://idp.example"),
                ("Auth:Oidc:Providers:company:ClientId", "client"),
                ("Auth:Oidc:Providers:company:ClientSecret", "secret")
            ),
            new TestEnvironment { EnvironmentName = Environments.Development }
        );

        Assert.Equal("http://127.0.0.1:3001", options.WebBaseUrl);
    }

    [Fact]
    public void Load_OAuth2StringValues_BindsEnums()
    {
        var options = OidcOptions.Load(
            Configuration(
                ("Auth:Oidc:PublicBaseUrl", "https://agw.example"),
                ("Auth:Oidc:Providers:github:Enabled", "true"),
                ("Auth:Oidc:Providers:github:Type", "OAuth2"),
                ("Auth:Oidc:Providers:github:AuthorizationEndpoint", "https://github.example/authorize"),
                ("Auth:Oidc:Providers:github:TokenEndpoint", "https://github.example/token"),
                ("Auth:Oidc:Providers:github:UserInfoEndpoint", "https://github.example/user"),
                ("Auth:Oidc:Providers:github:Issuer", "https://github.example"),
                ("Auth:Oidc:Providers:github:IdentitySource", "UserInfo"),
                ("Auth:Oidc:Providers:github:ClientAuthMethod", "Basic"),
                ("Auth:Oidc:Providers:github:ClientId", "client"),
                ("Auth:Oidc:Providers:github:ClientSecret", "secret")
            ),
            new TestEnvironment()
        );

        var provider = options.Providers["github"];
        Assert.Equal(AuthProviderType.OAuth2, provider.Type);
        Assert.Equal(OAuth2IdentitySource.UserInfo, provider.IdentitySource);
        Assert.Equal(OAuth2ClientAuthMethod.Basic, provider.ClientAuthMethod);
    }

    [Theory]
    [InlineData("Type", "1")]
    [InlineData("Type", "Unknown")]
    [InlineData("ClientAuthMethod", "2")]
    [InlineData("IdentitySource", "NoSuchSource")]
    public void Load_NumericOrUnknownEnumValue_Throws(string key, string value)
    {
        var values = new List<(string Key, string? Value)>
        {
            ("Auth:Oidc:PublicBaseUrl", "https://agw.example"),
            ("Auth:Oidc:Providers:github:Enabled", "true"),
            ("Auth:Oidc:Providers:github:AuthorizationEndpoint", "https://github.example/authorize"),
            ("Auth:Oidc:Providers:github:TokenEndpoint", "https://github.example/token"),
            ("Auth:Oidc:Providers:github:UserInfoEndpoint", "https://github.example/user"),
            ("Auth:Oidc:Providers:github:Issuer", "https://github.example"),
            ("Auth:Oidc:Providers:github:ClientId", "client"),
            ("Auth:Oidc:Providers:github:ClientSecret", "secret"),
        };
        if (!string.Equals(key, "Type", StringComparison.Ordinal))
            values.Add(("Auth:Oidc:Providers:github:Type", "OAuth2"));
        values.Add(($"Auth:Oidc:Providers:github:{key}", value));

        var exception = Assert.Throws<AgwException>(() =>
            OidcOptions.Load(Configuration(values.ToArray()), new TestEnvironment())
        );

        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_AccessTokenWithoutJwtSettings_Throws()
    {
        var exception = Assert.Throws<AgwException>(() =>
            OidcOptions.Load(
                Configuration(
                    ("Auth:Oidc:PublicBaseUrl", "https://agw.example"),
                    ("Auth:Oidc:Providers:internal:Enabled", "true"),
                    ("Auth:Oidc:Providers:internal:Type", "OAuth2"),
                    ("Auth:Oidc:Providers:internal:AuthorizationEndpoint", "https://sso.example/authorize"),
                    ("Auth:Oidc:Providers:internal:TokenEndpoint", "https://sso.example/token"),
                    ("Auth:Oidc:Providers:internal:Issuer", "https://sso.example"),
                    ("Auth:Oidc:Providers:internal:IdentitySource", "AccessToken"),
                    ("Auth:Oidc:Providers:internal:ClientId", "client"),
                    ("Auth:Oidc:Providers:internal:ClientSecret", "secret")
                ),
                new TestEnvironment()
            )
        );

        Assert.Contains("AccessTokenIssuer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_OAuth2EndpointOverLimit_Throws()
    {
        var endpoint = "https://sso.example/" + new string('a', 600);
        var exception = Assert.Throws<AgwException>(() =>
            OidcOptions.Load(
                Configuration(
                    ("Auth:Oidc:PublicBaseUrl", "https://agw.example"),
                    ("Auth:Oidc:Providers:internal:Enabled", "true"),
                    ("Auth:Oidc:Providers:internal:Type", "OAuth2"),
                    ("Auth:Oidc:Providers:internal:AuthorizationEndpoint", endpoint),
                    ("Auth:Oidc:Providers:internal:TokenEndpoint", "https://sso.example/token"),
                    ("Auth:Oidc:Providers:internal:UserInfoEndpoint", "https://sso.example/user"),
                    ("Auth:Oidc:Providers:internal:Issuer", "https://sso.example"),
                    ("Auth:Oidc:Providers:internal:ClientId", "client"),
                    ("Auth:Oidc:Providers:internal:ClientSecret", "secret")
                ),
                new TestEnvironment()
            )
        );

        Assert.Contains("AuthorizationEndpoint", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Agw.Auth.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
