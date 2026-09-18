using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class OAuth2IdentityServiceTests
{
    [Fact]
    public async Task AccessToken_ValidJwt_ReturnsConfiguredClaims()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var token = new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = "https://issuer.example",
                Audience = "agw",
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = "subject-1",
                    ["name"] = "Test User",
                    ["email"] = "user@example.com",
                },
                Expires = DateTime.UtcNow.AddMinutes(5),
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            }
        );
        var service = CreateService(Jwks(key));
        var identity = await service.ResolveAsync("internal", Provider(), token, TestContext.Current.CancellationToken);

        Assert.Equal("https://identity.example", identity.Issuer);
        Assert.Equal("subject-1", identity.Subject);
        Assert.Equal("Test User", identity.DisplayName);
        Assert.Equal("user@example.com", identity.Email);
    }

    [Fact]
    public async Task AccessToken_WrongIssuer_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var token = new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = "https://wrong.example",
                Audience = "agw",
                Claims = new Dictionary<string, object> { ["sub"] = "subject-1" },
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            }
        );
        var service = CreateService(Jwks(key));

        await Assert.ThrowsAsync<AgwException>(() =>
            service.ResolveAsync("internal", Provider(), token, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task AccessToken_WithoutExpiration_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = "https://issuer.example",
                Audience = "agw",
                Claims = new Dictionary<string, object> { ["sub"] = "subject-1" },
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            }
        );
        var service = CreateService(Jwks(key));

        await Assert.ThrowsAsync<AgwException>(() =>
            service.ResolveAsync("internal", Provider(), token, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task AccessToken_OpaqueValue_IsRejected()
    {
        var service = CreateService("{\"keys\":[]}");

        await Assert.ThrowsAsync<AgwException>(() =>
            service.ResolveAsync("internal", Provider(), "opaque-access-token", TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task UserInfo_NumericSubject_IsConvertedToString()
    {
        var client = new HttpClient(new StaticHandler("{\"id\":123,\"login\":\"octocat\"}"));
        var service = new OAuth2IdentityService(
            new StaticClientFactory(client),
            new MemoryCache(new MemoryCacheOptions())
        );
        var provider = new OidcProviderOptions
        {
            Type = AuthProviderType.OAuth2,
            IdentitySource = OAuth2IdentitySource.UserInfo,
            Issuer = "https://github.com/login/oauth",
            UserInfoEndpoint = "https://api.github.com/user",
            SubjectClaim = "id",
            DisplayNameClaim = "login",
        };

        var identity = await service.ResolveAsync(
            "github",
            provider,
            "access-token",
            TestContext.Current.CancellationToken
        );

        Assert.Equal("123", identity.Subject);
        Assert.Equal("octocat", identity.DisplayName);
    }

    [Fact]
    public async Task UserInfo_NonSuccessStatus_ThrowsCarryingStatusCode()
    {
        var client = new HttpClient(new StatusHandler(HttpStatusCode.Forbidden));
        var service = new OAuth2IdentityService(
            new StaticClientFactory(client),
            new MemoryCache(new MemoryCacheOptions())
        );
        var provider = new OidcProviderOptions
        {
            Type = AuthProviderType.OAuth2,
            IdentitySource = OAuth2IdentitySource.UserInfo,
            Issuer = "https://github.com/login/oauth",
            UserInfoEndpoint = "https://api.github.com/user",
            SubjectClaim = "id",
            DisplayNameClaim = "login",
        };

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.ResolveAsync("github", provider, "access-token", TestContext.Current.CancellationToken)
        );

        // The status code must survive so diagnostics report "provider-rejected", not a network outage.
        var httpException = Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Equal(HttpStatusCode.Forbidden, httpException.StatusCode);
    }

    private static OAuth2IdentityService CreateService(string jwks) =>
        new(
            new StaticClientFactory(new HttpClient(new StaticHandler(jwks))),
            new MemoryCache(new MemoryCacheOptions())
        );

    private static OidcProviderOptions Provider() =>
        new()
        {
            Type = AuthProviderType.OAuth2,
            IdentitySource = OAuth2IdentitySource.AccessToken,
            Issuer = "https://identity.example",
            AccessTokenIssuer = "https://issuer.example",
            AccessTokenAudience = "agw",
            AccessTokenJwksUri = "https://identity.example/.well-known/jwks.json",
            SubjectClaim = "sub",
            DisplayNameClaim = "name",
            EmailClaim = "email",
        };

    private static string Jwks(RsaSecurityKey key)
    {
        var parameters = ((RSA)key.Rsa!).ExportParameters(false);
        return JsonSerializer.Serialize(
            new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        kid = key.KeyId,
                        alg = "RS256",
                        n = Base64UrlEncoder.Encode(parameters.Modulus!),
                        e = Base64UrlEncoder.Encode(parameters.Exponent!),
                    },
                },
            }
        );
    }

    private sealed class StaticClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StaticClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly string _content;

        public StaticHandler(string content)
        {
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(JsonDocument.Parse(_content).RootElement.Clone()),
                }
            );
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StatusHandler(HttpStatusCode status)
        {
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(_status) { Content = JsonContent.Create(new { }) });
    }
}
