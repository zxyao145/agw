using System.Net;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class OidcFlowIntegrationTests
{
    [Fact]
    public async Task WebLogin_ValidProvider_CreatesOrdinaryCookieAndRejectsAdminOperation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var providers = await fixture.SendAsync(HttpMethod.Get, "/api/auth/oidc/providers");
        var catalog = (await Json(providers)).GetProperty("data");
        Assert.Equal(2, catalog.GetArrayLength());
        Assert.DoesNotContain("Secret", catalog.ToString(), StringComparison.OrdinalIgnoreCase);
        var callback = await fixture.BeginAsync("client=web&returnUrl=%2Fprojects%2F");
        var completed = await fixture.SendAsync(HttpMethod.Get, callback);
        Assert.True(
            completed.Headers.Location?.OriginalString == "/projects/",
            (fixture.Provider.AuthenticationFailure?.ToString() ?? "")
                + " Status: "
                + completed.StatusCode
                + " Location: "
                + completed.Headers.Location
                + " Body: "
                + await completed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var session = (await Json(await fixture.SendAsync(HttpMethod.Get, "/api/auth/session"))).GetProperty("data");
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal("10000", session.GetProperty("userId").GetString());
        Assert.False(session.GetProperty("isAdmin").GetBoolean());
        Assert.Equal("company", session.GetProperty("loginProvider").GetString());

        var csrf = (await Json(await fixture.SendAsync(HttpMethod.Get, "/api/auth/antiforgery")))
            .GetProperty("data")
            .GetProperty("requestToken")
            .GetString();
        var denied = await fixture.SendAsync(
            HttpMethod.Put,
            "/api/auth/password",
            new { currentPassword = "", newPassword = "not-admin-password" },
            csrf: csrf
        );
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(4030002, (await Json(denied)).GetProperty("code").GetInt32());
        var logout = await fixture.SendAsync(HttpMethod.Post, "/api/auth/logout", csrf: csrf);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        var anonymous = (await Json(await fixture.SendAsync(HttpMethod.Get, "/api/auth/session"))).GetProperty("data");
        Assert.False(anonymous.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task DesktopLogin_TwoIndependentProofs_IssuesOnlyAgwTokenAndRevokesIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var verifier = DesktopLoginProof.CreateCode();
        var challenge = DesktopLoginProof.Challenge(verifier);
        var state = DesktopLoginProof.CreateCode();
        var callback = await fixture.BeginAsync(
            "client=desktop&clientState=" + state + "&codeChallenge=" + challenge + "&codeChallengeMethod=S256"
        );
        Assert.NotEqual(challenge, fixture.Provider.ServerChallenge);
        var completed = await fixture.SendAsync(HttpMethod.Get, callback);
        var deepLink = completed.Headers.Location!;
        Assert.Equal("agw-desktop", deepLink.Scheme);
        var parameters = QueryHelpers.ParseQuery(deepLink.Query);
        Assert.Equal(state, parameters["state"].ToString());
        Assert.False(
            completed.Headers.TryGetValues("Set-Cookie", out var cookies)
                && cookies.Any(cookie => cookie.StartsWith("agw.session="))
        );
        Assert.True(fixture.Provider.ServerProofValidated);
        Assert.DoesNotContain("agw_", deepLink.ToString());
        var code = parameters["code"].ToString();

        var exchanged = await fixture.SendAsync(
            HttpMethod.Post,
            "/api/auth/desktop/exchange",
            new { code, codeVerifier = verifier },
            useCookies: false
        );
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        var credentials = (await Json(exchanged)).GetProperty("data");
        var token = credentials.GetProperty("token").GetString()!;
        Assert.StartsWith("agw_", token);
        Assert.False(credentials.TryGetProperty("id_token", out _));
        var repeated = await fixture.SendAsync(
            HttpMethod.Post,
            "/api/auth/desktop/exchange",
            new { code, codeVerifier = verifier },
            useCookies: false
        );
        Assert.Equal(HttpStatusCode.Unauthorized, repeated.StatusCode);
        var session = (
            await Json(await fixture.SendAsync(HttpMethod.Get, "/api/auth/session", bearer: token, useCookies: false))
        ).GetProperty("data");
        Assert.Equal("bearer", session.GetProperty("accessMode").GetString());
        Assert.Equal("10000", session.GetProperty("userId").GetString());
        var revoked = await fixture.SendAsync(
            HttpMethod.Post,
            "/api/auth/desktop/logout",
            bearer: token,
            useCookies: false
        );
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var anonymous = (
            await Json(await fixture.SendAsync(HttpMethod.Get, "/api/auth/session", bearer: token, useCookies: false))
        ).GetProperty("data");
        Assert.False(anonymous.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Callback_ProvisioningFails_DistinguishesLocalFailureWithoutSigningIn()
    {
        await using var fixture = await Fixture.CreateAsync(failProvisioning: true);
        var callback = await fixture.BeginAsync("client=web");
        var response = await fixture.SendAsync(HttpMethod.Get, callback);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=oidc-provisioning-failed", response.Headers.Location!.ToString());
        var session = (await Json(await fixture.SendAsync(HttpMethod.Get, "/api/auth/session"))).GetProperty("data");
        Assert.False(session.GetProperty("authenticated").GetBoolean());
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("state")]
    [InlineData("correlation")]
    public async Task Callback_InvalidProtocol_DoesNotProvisionUser(string failure)
    {
        await using var fixture = await Fixture.CreateAsync();
        var callback = await fixture.BeginAsync("client=web");
        fixture.Provider.Failure = failure;
        if (failure == "state")
            callback = "/api/auth/oidc/callback/company?code=any&state=invalid";
        var response = await fixture.SendAsync(HttpMethod.Get, callback, useCookies: failure != "correlation");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=oidc-", response.Headers.Location!.ToString());
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.Single(
            await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToArrayAsync(
                context.AuthUsers.IgnoreQueryFilters(),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/%5cevil.example")]
    public async Task Login_ExternalReturnUrl_RejectsRedirect(string target)
    {
        await using var fixture = await Fixture.CreateAsync();
        var response = await fixture.SendAsync(
            HttpMethod.Get,
            "/api/auth/oidc/login?providerId=company&returnUrl=" + Uri.EscapeDataString(target)
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        public WebApplication App { get; }
        public FakeProvider Provider { get; }

        private Fixture(WebApplication app, OidcIdentityStoreTests.TestDatabase database, FakeProvider provider)
        {
            App = app;
            _database = database;
            Provider = provider;
            _client = app.GetTestClient();
            _client.BaseAddress = new Uri("https://agw.example");
        }

        public static async Task<Fixture> CreateAsync(bool failProvisioning = false)
        {
            var database = await OidcIdentityStoreTests.TestDatabase.CreateAsync();
            var provider = new FakeProvider();
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { EnvironmentName = Environments.Production }
            );
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Auth:Oidc:PublicBaseUrl"] = "https://agw.example",
                    ["Auth:Oidc:Providers:company:Enabled"] = "true",
                    ["Auth:Oidc:Providers:company:Authority"] = "https://idp.example",
                    ["Auth:Oidc:Providers:company:ClientId"] = "agw-test",
                    ["Auth:Oidc:Providers:company:ClientSecret"] = "test-secret",
                    ["Auth:Oidc:Providers:second:Enabled"] = "true",
                    ["Auth:Oidc:Providers:second:Authority"] = "https://idp.example",
                    ["Auth:Oidc:Providers:second:ClientId"] = "agw-test",
                    ["Auth:Oidc:Providers:second:ClientSecret"] = "test-secret",
                }
            );
            builder.Services.AddAuth().AddOidcAuthentication(builder.Configuration, builder.Environment);
            builder.Services.PostConfigure<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(
                "oidc:company",
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
            builder.Services.AddHttpClient("Agw.Oidc").ConfigurePrimaryHttpMessageHandler(() => provider);
            builder.Services.AddSingleton<IServerInitializationState>(new Initialized());
            builder.Services.AddSingleton<IAuthenticationStateReader>(new AuthState());
            builder.Services.AddSingleton<IAuthenticationStateStore>(new AuthState());
            builder.Services.AddScoped(_ => database.Context());
            builder.Services.AddScoped<Agw.Projects.Application.Persistence.IProjectsDbContext>(sp =>
                sp.GetRequiredService<AgwDbContext>()
            );
            builder.Services.AddScoped<IUserProjectInitializer>(sp =>
                failProvisioning
                    ? new OidcIdentityStoreTests.FailingProjectInitializer(sp.GetRequiredService<AgwDbContext>())
                    : new UserProjectInitializer(sp.GetRequiredService<AgwDbContext>())
            );
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

        public async Task<string> BeginAsync(string query)
        {
            var redirect = await SendAsync(HttpMethod.Get, "/api/auth/oidc/login?providerId=company&" + query);
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
            var parameters = QueryHelpers.ParseQuery(redirect.Headers.Location!.Query);
            Provider.Nonce = parameters["nonce"].ToString();
            Provider.ServerChallenge = parameters["code_challenge"].ToString();
            return "/api/auth/oidc/callback/company?code=test-code&state="
                + Uri.EscapeDataString(parameters["state"].ToString());
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            object? body = null,
            string? csrf = null,
            string? bearer = null,
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
            if (csrf != null)
                request.Headers.Add("X-CSRF-TOKEN", csrf);
            if (bearer != null)
                request.Headers.Authorization = new("Bearer", bearer);
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

    private sealed class Initialized : IServerInitializationState
    {
        public bool IsInitialized => true;
    }

    private sealed class AuthState : IAuthenticationStateStore
    {
        public AuthenticationSnapshot GetAuthenticationSnapshot() => new("hash", 1);

        public Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Ordinary users cannot update the administrator password.");
    }

    private sealed class FakeProvider : HttpMessageHandler
    {
        private readonly RSA _rsa = RSA.Create(2048);
        public Exception? AuthenticationFailure { get; set; }
        public string Nonce { get; set; } = "";
        public string ServerChallenge { get; set; } = "";
        public bool ServerProofValidated { get; private set; }
        public string? Failure { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            object body;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/.well-known/openid-configuration":
                    body = new
                    {
                        issuer = "https://idp.example",
                        authorization_endpoint = "https://idp.example/authorize",
                        token_endpoint = "https://idp.example/token",
                        jwks_uri = "https://idp.example/keys",
                        response_types_supported = new[] { "code" },
                        subject_types_supported = new[] { "public" },
                        id_token_signing_alg_values_supported = new[] { "RS256" },
                    };
                    break;
                case "/keys":
                    var key = _rsa.ExportParameters(false);
                    body = new
                    {
                        keys = new[]
                        {
                            new
                            {
                                kty = "RSA",
                                use = "sig",
                                kid = "test",
                                alg = "RS256",
                                n = WebEncoders.Base64UrlEncode(key.Modulus!),
                                e = WebEncoders.Base64UrlEncode(key.Exponent!),
                            },
                        },
                    };
                    break;
                case "/token":
                    var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                    ServerProofValidated =
                        DesktopLoginProof.Challenge(form["code_verifier"].ToString()) == ServerChallenge
                        && form["client_secret"] == "test-secret"
                        && form["redirect_uri"] == "https://agw.example/api/auth/oidc/callback/company";
                    Assert.True(ServerProofValidated);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var payload = new
                    {
                        iss = Failure == "issuer" ? "https://evil.example" : "https://idp.example",
                        aud = Failure == "audience" ? "someone-else" : "agw-test",
                        sub = "external-person",
                        name = "External Person",
                        email = "person@example.com",
                        nonce = Failure == "nonce" ? "wrong-nonce" : Nonce,
                        iat = now - 3600,
                        exp = Failure == "expired" ? now - 1800 : now + 3600,
                    };
                    var header = WebEncoders.Base64UrlEncode(
                        JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "test" })
                    );
                    var token =
                        header + "." + WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
                    var signature = _rsa.SignData(
                        Encoding.ASCII.GetBytes(token),
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1
                    );
                    if (Failure == "signature")
                        signature[0] ^= 1;
                    body = new
                    {
                        token_type = "Bearer",
                        access_token = "idp-token-not-for-desktop",
                        expires_in = 3600,
                        id_token = token + "." + WebEncoders.Base64UrlEncode(signature),
                    };
                    break;
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _rsa.Dispose();
            base.Dispose(disposing);
        }
    }
}
