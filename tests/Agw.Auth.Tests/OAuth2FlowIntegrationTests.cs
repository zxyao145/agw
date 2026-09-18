using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Auth.Api;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Auth.Security;
using Agw.Infrastructure.Auth;
using Agw.Infrastructure.Data;
using Agw.Projects.Application;
using Agw.Projects.Contracts;
using Agw.Shared.Results;
using Agw.Shared.Runtime;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class OAuth2FlowIntegrationTests
{
    [Fact]
    public async Task UserInfo_PostPkce_CreatesCookieAndMapsNumericSubject()
    {
        await using var fixture = await Fixture.CreateAsync(usePkce: true, OAuth2ClientAuthMethod.Post);
        var callback = await fixture.BeginAsync();
        var response = await fixture.SendAsync(HttpMethod.Get, callback);

        Assert.True(
            response.StatusCode == HttpStatusCode.Redirect
                && response.Headers.Location?.OriginalString == "/dashboard/",
            fixture.Provider.AuthenticationFailure?.ToString() ?? response.Headers.Location?.ToString()
        );
        Assert.Contains("code_verifier=", fixture.Provider.TokenRequestBody);
        Assert.True(fixture.Provider.PkceValidated);
        Assert.Contains("client_id=oauth-client", fixture.Provider.TokenRequestBody);
        Assert.Contains("client_secret=oauth-secret", fixture.Provider.TokenRequestBody);
        Assert.Equal("Bearer oauth-access-token", fixture.Provider.UserInfoAuthorization);

        var session = (await Json(await fixture.SendAsync(HttpMethod.Get, "/api/auth/session"))).GetProperty("data");
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal("10000", session.GetProperty("userId").GetString());
        Assert.Equal("octocat", session.GetProperty("displayName").GetString());
        Assert.Equal("github", session.GetProperty("loginProvider").GetString());
    }

    [Fact]
    public async Task UserInfo_SendsUserAgentHeader()
    {
        await using var fixture = await Fixture.CreateAsync(usePkce: false, OAuth2ClientAuthMethod.Post);
        var callback = await fixture.BeginAsync();
        await fixture.SendAsync(HttpMethod.Get, callback);

        // GitHub's REST API answers 403 when no User-Agent is present; .NET sends none by default.
        Assert.False(string.IsNullOrWhiteSpace(fixture.Provider.UserInfoUserAgent));
    }

    [Fact]
    public async Task UserInfo_WebBaseUrlConfigured_RedirectsToWebOrigin()
    {
        await using var fixture = await Fixture.CreateAsync(
            usePkce: true,
            OAuth2ClientAuthMethod.Post,
            webBaseUrl: "https://app.example"
        );
        var callback = await fixture.BeginAsync();
        var response = await fixture.SendAsync(HttpMethod.Get, callback);

        // Split topology: the callback runs on PublicBaseUrl but the browser must land on the SPA.
        Assert.Equal("https://app.example/dashboard/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task UserInfo_BasicWithoutPkce_UsesBasicClientAuthentication()
    {
        await using var fixture = await Fixture.CreateAsync(
            usePkce: false,
            OAuth2ClientAuthMethod.Basic,
            formResponse: true,
            clientId: "client:with space",
            clientSecret: "secret%value"
        );
        var callback = await fixture.BeginAsync();
        var response = await fixture.SendAsync(HttpMethod.Get, callback);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("code_verifier=", fixture.Provider.TokenRequestBody);
        Assert.Equal("Basic", fixture.Provider.TokenAuthorization?.Scheme);
        var credentials = fixture.Provider.TokenAuthorization?.Parameter;
        Assert.NotNull(credentials);
        Assert.Equal(
            "client%3Awith+space:secret%25value",
            Encoding.UTF8.GetString(Convert.FromBase64String(credentials!))
        );
    }

    [Fact]
    public async Task PostCallback_IsRejectedBeforeTokenExchange()
    {
        await using var fixture = await Fixture.CreateAsync(usePkce: true, OAuth2ClientAuthMethod.Post);
        var callback = await fixture.BeginAsync();
        var response = await fixture.SendAsync(HttpMethod.Post, callback);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(fixture.Provider.TokenRequestBody);
    }

    [Fact]
    public async Task AccessTokenJwt_ValidatesSignatureAndClaims()
    {
        await using var fixture = await Fixture.CreateAsync(
            usePkce: true,
            OAuth2ClientAuthMethod.Post,
            identitySource: OAuth2IdentitySource.AccessToken
        );
        var callback = await fixture.BeginAsync();
        var response = await fixture.SendAsync(HttpMethod.Get, callback);

        Assert.True(
            response.Headers.Location?.OriginalString == "/dashboard/",
            fixture.Provider.AuthenticationFailure?.ToString() ?? response.Headers.Location?.ToString()
        );
        Assert.True(fixture.Provider.PkceValidated);
        var session = (await Json(await fixture.SendAsync(HttpMethod.Get, "/api/auth/session"))).GetProperty("data");
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal("10000", session.GetProperty("userId").GetString());
        Assert.Equal("jwt-user", session.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task TokenEndpoint_200WithoutAccessToken_IsRejected()
    {
        await using var fixture = await Fixture.CreateAsync(
            usePkce: false,
            OAuth2ClientAuthMethod.Post,
            invalidTokenResponse: true
        );
        var callback = await fixture.BeginAsync();
        var response = await fixture.SendAsync(HttpMethod.Get, callback);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=oidc-", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task DesktopLogin_CreatesOneTimeGrantAndAgwToken()
    {
        await using var fixture = await Fixture.CreateAsync(usePkce: true, OAuth2ClientAuthMethod.Post);
        var verifier = DesktopLoginProof.CreateCode();
        var challenge = DesktopLoginProof.Challenge(verifier);
        var state = DesktopLoginProof.CreateCode();
        var callback = await fixture.BeginAsync(
            "client=desktop&clientState="
                + Uri.EscapeDataString(state)
                + "&codeChallenge="
                + Uri.EscapeDataString(challenge)
                + "&codeChallengeMethod=S256"
        );

        var completed = await fixture.SendAsync(HttpMethod.Get, callback);
        var deepLink = completed.Headers.Location!;
        Assert.Equal("agw-desktop", deepLink.Scheme);
        var parameters = QueryHelpers.ParseQuery(deepLink.Query);
        Assert.Equal(state, parameters["state"].ToString());
        Assert.False(
            completed.Headers.TryGetValues("Set-Cookie", out var cookies)
                && cookies.Any(cookie => cookie.StartsWith("agw.session=", StringComparison.Ordinal))
        );

        var exchanged = await fixture.SendAsync(
            HttpMethod.Post,
            "/api/auth/desktop/exchange",
            new { code = parameters["code"].ToString(), codeVerifier = verifier },
            useCookies: false
        );
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        Assert.StartsWith("agw_", (await Json(exchanged)).GetProperty("data").GetProperty("token").GetString()!);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument
            .Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.Clone();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly OidcIdentityStoreTests.TestDatabase _database;
        private readonly HttpClient _client;
        private readonly CookieContainer _cookies = new();

        private Fixture(WebApplication app, OidcIdentityStoreTests.TestDatabase database, FakeOAuthProvider provider)
        {
            App = app;
            _database = database;
            Provider = provider;
            _client = app.GetTestClient();
            _client.BaseAddress = new Uri("https://agw.example");
        }

        public WebApplication App { get; }
        public FakeOAuthProvider Provider { get; }

        public static async Task<Fixture> CreateAsync(
            bool usePkce,
            OAuth2ClientAuthMethod clientAuthMethod,
            bool formResponse = false,
            OAuth2IdentitySource identitySource = OAuth2IdentitySource.UserInfo,
            string clientId = "oauth-client",
            string clientSecret = "oauth-secret",
            bool invalidTokenResponse = false,
            string? webBaseUrl = null
        )
        {
            var database = await OidcIdentityStoreTests.TestDatabase.CreateAsync();
            var provider = new FakeOAuthProvider(formResponse, usePkce, identitySource, invalidTokenResponse);
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { EnvironmentName = Environments.Production }
            );
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Auth:Oidc:PublicBaseUrl"] = "https://agw.example",
                    ["Auth:Oidc:WebBaseUrl"] = webBaseUrl ?? "",
                    ["Auth:Oidc:Providers:github:Enabled"] = "true",
                    ["Auth:Oidc:Providers:github:Type"] = "OAuth2",
                    ["Auth:Oidc:Providers:github:DisplayName"] = "GitHub",
                    ["Auth:Oidc:Providers:github:AuthorizationEndpoint"] = "https://oauth.example/authorize",
                    ["Auth:Oidc:Providers:github:TokenEndpoint"] = "https://oauth.example/oauth/token",
                    ["Auth:Oidc:Providers:github:UserInfoEndpoint"] =
                        identitySource == OAuth2IdentitySource.UserInfo ? "https://oauth.example/user" : "",
                    ["Auth:Oidc:Providers:github:Issuer"] = "https://oauth.example",
                    ["Auth:Oidc:Providers:github:IdentitySource"] = identitySource.ToString(),
                    ["Auth:Oidc:Providers:github:UsePkce"] = usePkce.ToString(),
                    ["Auth:Oidc:Providers:github:ClientAuthMethod"] = clientAuthMethod.ToString(),
                    ["Auth:Oidc:Providers:github:Scopes:0"] = "read:user",
                    ["Auth:Oidc:Providers:github:SubjectClaim"] =
                        identitySource == OAuth2IdentitySource.AccessToken ? "sub" : "id",
                    ["Auth:Oidc:Providers:github:DisplayNameClaim"] =
                        identitySource == OAuth2IdentitySource.AccessToken ? "name" : "login",
                    ["Auth:Oidc:Providers:github:EmailClaim"] = "email",
                    ["Auth:Oidc:Providers:github:AccessTokenIssuer"] = "https://oauth.example",
                    ["Auth:Oidc:Providers:github:AccessTokenAudience"] = "agw",
                    ["Auth:Oidc:Providers:github:AccessTokenJwksUri"] = "https://oauth.example/oauth/keys",
                    ["Auth:Oidc:Providers:github:ClientId"] = clientId,
                    ["Auth:Oidc:Providers:github:ClientSecret"] = clientSecret,
                }
            );
            builder.Services.AddAuth().AddOidcAuthentication(builder.Configuration, builder.Environment);
            builder.Services.AddHttpClient("Agw.Oidc").ConfigurePrimaryHttpMessageHandler(() => provider);
            builder.Services.PostConfigure<AgwOAuthOptions>(
                "oauth2:github",
                options =>
                {
                    var failure = options.Events.OnRemoteFailure;
                    options.Events.OnRemoteFailure = async context =>
                    {
                        provider.AuthenticationFailure = context.Failure;
                        await failure(context);
                    };
                }
            );
            builder.Services.AddSingleton<IServerInitializationState>(new Initialized());
            builder.Services.AddSingleton<IAuthenticationStateReader>(new AuthState());
            builder.Services.AddSingleton<IAuthenticationStateStore>(new AuthState());
            builder.Services.AddScoped(_ => database.Context());
            builder.Services.AddScoped<Agw.Projects.Application.Persistence.IProjectsDbContext>(sp =>
                sp.GetRequiredService<AgwDbContext>()
            );
            builder.Services.AddScoped<IUserProjectInitializer, UserProjectInitializer>();
            builder.Services.AddScoped<IApiTokenStore, EfApiTokenStore>();
            builder.Services.AddScoped<IOidcIdentityStore, EfOidcIdentityStore>();
            builder.Services.AddControllersWithViews().AddApplicationPart(typeof(AuthController).Assembly);
            builder.Services.AddApiResult();
            var app = builder.Build();
            app.UseMiddleware<AgwApiExceptionMiddleware>();
            app.UseRouting();
            app.UseAgwAuth();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync(TestContext.Current.CancellationToken);
            return new Fixture(app, database, provider);
        }

        public async Task<string> BeginAsync(string query = "client=web&returnUrl=%2Fdashboard%2F")
        {
            var response = await SendAsync(HttpMethod.Get, "/api/auth/oidc/login?providerId=github&" + query);
            Assert.True(
                response.StatusCode == HttpStatusCode.Redirect,
                Provider.AuthenticationFailure?.ToString() ?? response.Headers.Location?.ToString()
            );
            var parameters = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            var method = parameters.TryGetValue("code_challenge_method", out var methodValue)
                ? methodValue.ToString()
                : string.Empty;
            Assert.True(
                method == (Provider.ExpectedPkce ? "S256" : string.Empty),
                response.Headers.Location!.ToString()
            );
            Provider.CodeChallenge = parameters.TryGetValue("code_challenge", out var challenge)
                ? challenge.ToString()
                : string.Empty;
            Provider.State = parameters["state"].ToString();
            return "/api/auth/oidc/callback/github?code=test-code&state=" + Uri.EscapeDataString(Provider.State);
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            object? body = null,
            bool useCookies = true
        )
        {
            using var request = new HttpRequestMessage(method, path);
            if (body != null)
                request.Content = JsonContent.Create(body);
            if (useCookies)
                request.Headers.TryAddWithoutValidation(
                    "Cookie",
                    _cookies.GetCookieHeader(new Uri(_client.BaseAddress!, path))
                );
            var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
            if (useCookies && response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values)
                    _cookies.SetCookies(new Uri(_client.BaseAddress!, path), value);
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await App.DisposeAsync();
            Provider.Dispose();
            await _database.DisposeAsync();
        }
    }

    private sealed class FakeOAuthProvider : HttpMessageHandler
    {
        private readonly bool _formResponse;
        private readonly OAuth2IdentitySource _identitySource;
        private readonly bool _invalidTokenResponse;
        private readonly RSA _rsa = RSA.Create(2048);

        public FakeOAuthProvider(
            bool formResponse,
            bool expectedPkce,
            OAuth2IdentitySource identitySource,
            bool invalidTokenResponse
        )
        {
            _formResponse = formResponse;
            ExpectedPkce = expectedPkce;
            _identitySource = identitySource;
            _invalidTokenResponse = invalidTokenResponse;
        }

        public bool ExpectedPkce { get; }
        public string CodeChallenge { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string TokenRequestBody { get; private set; } = string.Empty;
        public AuthenticationHeaderValue? TokenAuthorization { get; private set; }
        public string? UserInfoAuthorization { get; private set; }
        public string? UserInfoUserAgent { get; private set; }
        public bool PkceValidated { get; private set; }
        public Exception? AuthenticationFailure { get; set; }

        private string JwtAccessToken =>
            new JsonWebTokenHandler().CreateToken(
                new SecurityTokenDescriptor
                {
                    Issuer = "https://oauth.example",
                    Audience = "agw",
                    Claims = new Dictionary<string, object>
                    {
                        ["sub"] = "jwt-subject",
                        ["name"] = "jwt-user",
                        ["email"] = "jwt@example.com",
                    },
                    Expires = DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = new SigningCredentials(
                        new RsaSecurityKey(_rsa) { KeyId = "oauth-test-key" },
                        SecurityAlgorithms.RsaSha256
                    ),
                }
            );

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.RequestUri?.AbsolutePath == "/oauth/token")
            {
                TokenRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                TokenAuthorization = request.Headers.Authorization;
                var values = QueryHelpers.ParseQuery(TokenRequestBody);
                PkceValidated = ExpectedPkce
                    ? values.TryGetValue("code_verifier", out var verifier)
                        && Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier.ToString())))
                            == CodeChallenge
                    : !values.ContainsKey("code_verifier");
                var accessToken =
                    _identitySource == OAuth2IdentitySource.AccessToken ? JwtAccessToken : "oauth-access-token";
                var content =
                    _invalidTokenResponse ? new StringContent("<html>not-json</html>", Encoding.UTF8, "text/html")
                    : _formResponse
                        ? new StringContent(
                            "access_token=" + Uri.EscapeDataString(accessToken) + "&token_type=Bearer",
                            Encoding.UTF8,
                            "application/x-www-form-urlencoded"
                        )
                    : new StringContent(
                        "{\"access_token\":\"" + accessToken + "\",\"token_type\":\"Bearer\"}",
                        Encoding.UTF8,
                        "application/json"
                    );
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            if (request.RequestUri?.AbsolutePath == "/user")
            {
                UserInfoAuthorization = request.Headers.Authorization?.ToString();
                UserInfoUserAgent = request.Headers.UserAgent.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"id\":12345,\"login\":\"octocat\",\"name\":\"GitHub user\",\"email\":\"user@example.com\"}",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            }

            if (request.RequestUri?.AbsolutePath == "/oauth/keys")
            {
                var parameters = _rsa.ExportParameters(false);
                var jwks = JsonSerializer.Serialize(
                    new
                    {
                        keys = new[]
                        {
                            new
                            {
                                kty = "RSA",
                                use = "sig",
                                kid = "oauth-test-key",
                                alg = "RS256",
                                n = Base64UrlEncoder.Encode(parameters.Modulus!),
                                e = Base64UrlEncoder.Encode(parameters.Exponent!),
                            },
                        },
                    }
                );
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(jwks, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _rsa.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class Initialized : IServerInitializationState
    {
        public bool IsInitialized => true;
    }

    private sealed class AuthState : IAuthenticationStateStore
    {
        public AuthenticationSnapshot GetAuthenticationSnapshot() => new("hash", 1);

        public Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
