using System.Net;
using System.Text;

using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Contracts.Manager;
using Agw.Shared.Data.Entities.Integrations;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Integrations.Controllers.Tests;

public class OauthControllerTests
{
    [Fact]
    public async Task AuthorizeStartAsync_WhenInstanceExists_ReturnsAuthorizeUrlAndSetsCallbackCookie()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appInstanceId = Guid.NewGuid();

        await using var scope = await OAuthControllerTestScope.CreateAsync(
            cancellationToken,
            dbContext =>
            {
                dbContext.AppInstances.Add(new AppInstance
                {
                    Id = appInstanceId,
                    AppName = "github",
                    ClientId = "client-id",
                    ClientSecret = "client-secret",
                    UsePkce = true,
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                });
            });

        var controller = scope.CreateIntegrationsController(httpContext =>
        {
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("backend.example.com");
            httpContext.Request.Headers.Origin = "https://frontend.example.com";
        });

        var result = await controller.AuthorizeStartAsync(appInstanceId, cancellationToken);

        var payload = Assert.IsType<AuthorizeStartResponse>(ReadApiResultData(result));
        Assert.Contains("client_id=client-id", payload.AuthorizeUrl);
        Assert.Contains(
            "redirect_uri=https%3A%2F%2Ffrontend.example.com%2Fapi%2Fintegrations%2Foauth%2Fcallback",
            payload.AuthorizeUrl);
        Assert.Contains("state=", payload.AuthorizeUrl);
        Assert.Contains("code_challenge_method=S256", payload.AuthorizeUrl);
        Assert.Contains("Set-Cookie", controller.Response.Headers.Keys);
        Assert.Contains("frontend.example.com", controller.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task AuthorizeStartAsync_WhenInstanceDoesNotExist_ReturnsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OAuthControllerTestScope.CreateAsync(cancellationToken);
        var controller = scope.CreateIntegrationsController();

        var result = await controller.AuthorizeStartAsync(Guid.NewGuid(), cancellationToken);

        AssertApiResult(result);
    }

    [Fact]
    public async Task OAuthCallback_WhenStateMapsToAppInstance_StoresTokenForThatInstance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appInstanceId = Guid.NewGuid();
        const string state = "state-123";

        await using var scope = await OAuthControllerTestScope.CreateAsync(
            cancellationToken,
            dbContext =>
            {
                dbContext.AppInstances.Add(new AppInstance
                {
                    Id = appInstanceId,
                    AppName = "github",
                    ClientId = "client-id",
                    ClientSecret = "client-secret",
                    UsePkce = true,
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                });
            },
            new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"access_token":"access-token","refresh_token":"refresh-token","token_type":"Bearer","expires_in":3600,"sub":"user-123"}
                        """,
                        Encoding.UTF8,
                        "application/json")
                }));

        var controller = scope.CreateOauthController(httpContext =>
        {
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("localhost:5015");
            httpContext.Request.Path = "/api/integrations/oauth/callback";
            httpContext.Request.QueryString = new QueryString($"?code=test-code&state={state}");
            httpContext.Request.Headers.Cookie = $"{BuildCallbackStateCookieName(state)}={BuildCallbackCookieValue(appInstanceId, "github", state, "verifier-123")}";
        });

        var result = await controller.OAuthCallback(cancellationToken);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.StartsWith("https://frontend.example.com/integrations/callback", redirect.Url);
        Assert.Contains("exchange_status=success", redirect.Url);
        Assert.Contains("provider=github", redirect.Url);
        Assert.Contains("subject=user-123", redirect.Url);

        await using var assertContext = scope.CreateDbContext();
        var token = await assertContext.OAuthAuthorizationTokens.SingleAsync(
            entity => entity.AppInstanceId == appInstanceId,
            cancellationToken);

        Assert.Equal("access-token", token.AccessToken);
        Assert.Equal("refresh-token", token.RefreshToken);
        Assert.Equal("user-123", token.Subject);
    }

    private static string BuildCallbackStateCookieName(string state)
    {
        return $"agw_oauth2_{WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(state))}";
    }

    private static object ReadApiResultData(IActionResult result)
    {
        AssertApiResult(result);
        var property = result.GetType().GetProperty("Data");
        Assert.NotNull(property);

        var data = property!.GetValue(result);
        Assert.NotNull(data);
        return data;
    }

    private static void AssertApiResult(IActionResult result)
    {
        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
    }

    private static string BuildCallbackCookieValue(
        Guid appInstanceId,
        string integrationId,
        string state,
        string? verifier = null,
        string? uiCallbackUrl = "https://frontend.example.com/integrations/callback")
    {
        var payload =
            $$"""
              {"appInstanceId":"{{appInstanceId}}","integrationId":"{{integrationId}}","state":"{{state}}","verifier":"{{verifier}}","uiCallbackUrl":"{{uiCallbackUrl}}","createdAt":"{{DateTime.UtcNow:O}}"}
              """;

        return Uri.EscapeDataString(payload);
    }

    private sealed class OAuthControllerTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly IHttpClientFactory _httpClientFactory;

        private OAuthControllerTestScope(
            SqliteConnection connection,
            DbContextOptions<AgwDbContext> options,
            IHttpClientFactory httpClientFactory)
        {
            _connection = connection;
            _options = options;
            _httpClientFactory = httpClientFactory;
        }

        public static async Task<OAuthControllerTestScope> CreateAsync(
            CancellationToken cancellationToken,
            Action<AgwDbContext>? seed = null,
            HttpMessageHandler? handler = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);

            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;

            await using (var setupContext = new AgwDbContext(options))
            {
                await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            }

            if (seed != null)
            {
                await using var seedContext = new AgwDbContext(options);
                seed(seedContext);
                await seedContext.SaveChangesAsync(cancellationToken);
            }

            var httpClientFactory = new StubHttpClientFactory(handler ?? new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)));

            return new OAuthControllerTestScope(connection, options, httpClientFactory);
        }

        public AgwDbContext CreateDbContext() => new(_options);

        public IntegrationsController CreateIntegrationsController(Action<DefaultHttpContext>? configure = null)
        {
            var dbContext = CreateDbContext();
            var controller = new IntegrationsController(
                new AppDefinitionRepo(),
                new EfRepository<AppInstance>(dbContext),
                new UnitOfWork(dbContext));

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(configure)
            };

            return controller;
        }

        public OAuthController CreateOauthController(Action<DefaultHttpContext>? configure = null)
        {
            var dbContext = CreateDbContext();
            var controller = new OAuthController(
                new ConfigurationBuilder().Build(),
                _httpClientFactory,
                new AppDefinitionRepo(),
                new EfRepository<AppInstance>(dbContext),
                new EfRepository<OAuthAuthorizationToken>(dbContext),
                new UnitOfWork(dbContext),
                NullLogger<OAuthController>.Instance);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(configure)
            };

            return controller;
        }

        private static DefaultHttpContext CreateHttpContext(Action<DefaultHttpContext>? configure)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers.Origin = "https://frontend.example.com";
            configure?.Invoke(httpContext);
            return httpContext;
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _httpClient = new HttpClient(handler, disposeHandler: true);
        }

        public HttpClient CreateClient(string name = "")
        {
            return _httpClient;
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
