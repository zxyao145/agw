using System.Net;
using System.Text;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Encryption;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Application.Management;
using Agw.Integrations.Application.OAuth;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Contracts.OAuth;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using IntegrationConnection = Agw.Shared.Data.Entities.Integrations.Connection;

namespace Agw.Integrations.Tests;

public sealed class OAuthAppServiceTests
{
    private const string CallbackUri = "https://agw.test/api/integrations/oauth/callback";

    [Fact]
    public async Task StartAsync_PkceEnabled_ReturnsOpaqueStateAndS256Challenge()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.None,
            OAuthSubjectSource.TokenResponse,
            cancellationToken
        );

        var response = await scope.Authorization.StartAsync(
            scope.ConnectionId,
            CallbackUri,
            "/integrations?source=settings",
            OAuthCompletionTarget.Web,
            "tester",
            cancellationToken
        );

        var uri = new Uri(response.AuthorizationUrl);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal("oauth-client", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(CallbackUri, query["redirect_uri"]);
        Assert.Equal("repo read:user", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(StringValues.IsNullOrEmpty(query["code_challenge"]));
        Assert.False(StringValues.IsNullOrEmpty(query["state"]));
        Assert.DoesNotContain(
            scope.ConnectionId.ToString(),
            query["state"].ToString(),
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal("fixed", query["prompt"]);

        var connection = await scope.DbContext.Connections.SingleAsync(
            item => item.Id == scope.ConnectionId,
            cancellationToken
        );
        Assert.Equal(ConnectionStatus.PendingAuthorization, connection.Status);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/%2Fevil.example")]
    [InlineData("/%252Fevil.example")]
    [InlineData("/safe%5Cunsafe")]
    [InlineData("/safe%255Cunsafe")]
    public async Task StartAsync_UnsafeReturnPath_RejectsBeforeChangingConnection(string returnPath)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.None,
            OAuthSubjectSource.TokenResponse,
            cancellationToken
        );

        await Assert.ThrowsAsync<AgwException>(() =>
            scope.Authorization.StartAsync(
                scope.ConnectionId,
                CallbackUri,
                returnPath,
                OAuthCompletionTarget.Web,
                "tester",
                cancellationToken
            )
        );

        var connection = await scope.DbContext.Connections.SingleAsync(cancellationToken);
        Assert.Equal(ConnectionStatus.Unverified, connection.Status);
    }

    [Fact]
    public async Task Callback_BodyClientAuthentication_StoresProtectedTokensForStateConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.Body,
            OAuthSubjectSource.TokenResponse,
            cancellationToken
        );
        const string accessToken = "access-token-sensitive";
        const string refreshToken = "refresh-token-sensitive";
        const string idToken = "id-token-sensitive";
        var otherConnectionId = await scope.SeedOtherConnectionAsync(cancellationToken);
        scope.Handler.EnqueueJson(
            HttpStatusCode.OK,
            $$"""
            {"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","id_token":"{{idToken}}","expires_in":3600,"account":{"name":"octocat"},"token_type":"Bearer"}
            """
        );
        var started = await scope.Authorization.StartAsync(
            scope.ConnectionId,
            CallbackUri,
            "/integrations",
            OAuthCompletionTarget.Desktop,
            "tester",
            cancellationToken
        );
        var state = QueryHelpers.ParseQuery(new Uri(started.AuthorizationUrl).Query)["state"].ToString();
        var challenge = QueryHelpers.ParseQuery(new Uri(started.AuthorizationUrl).Query)["code_challenge"].ToString();

        var result = await scope.Authorization.HandleCallbackAsync(
            state,
            "authorization-code-sensitive",
            null,
            "tester",
            cancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(OAuthCompletionTarget.Desktop, result.CompletionTarget);
        Assert.DoesNotContain("authorization-code-sensitive", result.RedirectPath, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, result.RedirectPath, StringComparison.Ordinal);
        var request = Assert.Single(scope.Handler.Requests);
        Assert.Null(request.Authorization);
        var form = QueryHelpers.ParseQuery(request.Body);
        Assert.Equal("oauth-client", form["client_id"]);
        Assert.Equal("oauth-secret", form["client_secret"]);
        Assert.Equal("authorization-code-sensitive", form["code"]);
        Assert.Equal(CallbackUri, form["redirect_uri"]);
        Assert.NotEqual(challenge, form["code_verifier"].ToString());

        scope.DbContext.ChangeTracker.Clear();
        var connection = await scope.DbContext.Connections.SingleAsync(
            item => item.Id == scope.ConnectionId,
            cancellationToken
        );
        Assert.Equal(ConnectionStatus.Ready, connection.Status);
        Assert.Equal("octocat", connection.Subject);
        Assert.NotNull(connection.LastValidatedAtUtc);
        var credentials = await scope
            .DbContext.ConnectionCredentials.Where(item => item.ConnectionId == scope.ConnectionId)
            .ToDictionaryAsync(item => item.Slot, cancellationToken);
        Assert.Equal(3, credentials.Count);
        AssertProtected(scope, credentials[IntegrationCredentialSlots.OAuthAccessToken], accessToken);
        AssertProtected(scope, credentials[IntegrationCredentialSlots.OAuthRefreshToken], refreshToken);
        AssertProtected(scope, credentials[IntegrationCredentialSlots.OAuthIdToken], idToken);
        Assert.Equal(scope.Now.AddHours(1), credentials[IntegrationCredentialSlots.OAuthAccessToken].ExpiresAtUtc);
        Assert.False(
            await scope.DbContext.ConnectionCredentials.AnyAsync(
                item => item.ConnectionId == otherConnectionId,
                cancellationToken
            )
        );
    }

    [Fact]
    public async Task Callback_BasicClientAuthentication_UsesHeaderAndOmitsBodyCredentials()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.Basic,
            OAuthSubjectSource.TokenResponse,
            cancellationToken
        );
        scope.Handler.EnqueueJson(HttpStatusCode.OK, "{\"access_token\":\"token\",\"account\":{\"name\":\"user\"}}");
        var state = await StartAndReadStateAsync(scope, cancellationToken);

        await scope.Authorization.HandleCallbackAsync(state, "code", null, "tester", cancellationToken);

        var request = Assert.Single(scope.Handler.Requests);
        Assert.Equal("Basic", request.Authorization?.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("oauth-client:oauth-secret")),
            request.Authorization?.Parameter
        );
        var form = QueryHelpers.ParseQuery(request.Body);
        Assert.False(form.ContainsKey("client_id"));
        Assert.False(form.ContainsKey("client_secret"));
    }

    [Fact]
    public async Task Callback_BasicClientAuthentication_FormEncodesCredentialsBeforeBase64()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string clientId = "client id:with%chars";
        const string clientSecret = "secret value:with%chars";
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.Basic,
            OAuthSubjectSource.TokenResponse,
            cancellationToken,
            clientId: clientId,
            clientSecret: clientSecret
        );
        scope.Handler.EnqueueJson(HttpStatusCode.OK, "{\"access_token\":\"token\",\"account\":{\"name\":\"user\"}}");
        var state = await StartAndReadStateAsync(scope, cancellationToken);

        await scope.Authorization.HandleCallbackAsync(state, "code", null, "tester", cancellationToken);

        var request = Assert.Single(scope.Handler.Requests);
        var expectedCredentials = $"{WebUtility.UrlEncode(clientId)}:{WebUtility.UrlEncode(clientSecret)}";
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(expectedCredentials)),
            request.Authorization?.Parameter
        );
    }

    [Theory]
    [InlineData(
        OAuthSubjectSource.TokenResponse,
        "{\"access_token\":\"token\",\"account\":{\"name\":\"token-user\"}}",
        "token-user"
    )]
    [InlineData(
        OAuthSubjectSource.IdToken,
        "{\"access_token\":\"token\",\"id_token\":\"eyJhbGciOiJub25lIn0.eyJhY2NvdW50Ijp7Im5hbWUiOiJqd3QtdXNlciJ9fQ.\"}",
        "jwt-user"
    )]
    public async Task Callback_SubjectSource_ResolvesConfiguredJsonPath(
        OAuthSubjectSource source,
        string tokenResponse,
        string expectedSubject
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.None,
            source,
            cancellationToken
        );
        scope.Handler.EnqueueJson(HttpStatusCode.OK, tokenResponse);
        var state = await StartAndReadStateAsync(scope, cancellationToken);

        var result = await scope.Authorization.HandleCallbackAsync(state, "code", null, "tester", cancellationToken);

        Assert.True(result.Success);
        var connection = await scope.DbContext.Connections.SingleAsync(cancellationToken);
        Assert.Equal(expectedSubject, connection.Subject);
    }

    [Fact]
    public async Task Callback_UserInfoSubject_UsesNewAccessToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.None,
            OAuthSubjectSource.UserInfo,
            cancellationToken
        );
        scope.Handler.EnqueueJson(HttpStatusCode.OK, "{\"access_token\":\"userinfo-token\"}");
        scope.Handler.EnqueueJson(HttpStatusCode.OK, "{\"account\":{\"name\":\"userinfo-user\"}}");
        var state = await StartAndReadStateAsync(scope, cancellationToken);

        var result = await scope.Authorization.HandleCallbackAsync(state, "code", null, "tester", cancellationToken);

        Assert.True(result.Success);
        Assert.Equal(2, scope.Handler.Requests.Count);
        Assert.Equal("Bearer", scope.Handler.Requests[1].Authorization?.Scheme);
        Assert.Equal("userinfo-token", scope.Handler.Requests[1].Authorization?.Parameter);
        Assert.NotEmpty(scope.Handler.Requests[1].UserAgent);
        var connection = await scope.DbContext.Connections.SingleAsync(cancellationToken);
        Assert.Equal("userinfo-user", connection.Subject);
    }

    [Fact]
    public async Task Refresh_TokenResponseOmitsRefreshToken_PreservesExistingRefreshToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.Body,
            OAuthSubjectSource.TokenResponse,
            cancellationToken,
            supportsRefresh: true
        );
        await scope.SeedConnectionCredentialAsync(
            IntegrationCredentialSlots.OAuthAccessToken,
            "old-access",
            scope.Now.AddMinutes(-1),
            cancellationToken
        );
        await scope.SeedConnectionCredentialAsync(
            IntegrationCredentialSlots.OAuthRefreshToken,
            "old-refresh",
            null,
            cancellationToken
        );
        scope.Handler.EnqueueJson(HttpStatusCode.OK, "{\"access_token\":\"new-access\",\"expires_in\":7200}");

        var response = await scope.Refresh.RefreshAsync(scope.ConnectionId, "tester", cancellationToken);

        Assert.Equal(scope.Now.AddHours(2), response.ExpiresAtUtc);
        var request = Assert.Single(scope.Handler.Requests);
        var form = QueryHelpers.ParseQuery(request.Body);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("old-refresh", form["refresh_token"]);
        scope.DbContext.ChangeTracker.Clear();
        var access = await scope.Reader.ReadConnectionAsync(
            scope.ConnectionId,
            IntegrationCredentialSlots.OAuthAccessToken,
            cancellationToken
        );
        var refresh = await scope.Reader.ReadConnectionAsync(
            scope.ConnectionId,
            IntegrationCredentialSlots.OAuthRefreshToken,
            cancellationToken
        );
        Assert.Equal("new-access", access!.Value);
        Assert.Equal("old-refresh", refresh!.Value);
    }

    [Fact]
    public async Task Callback_TokenEndpointError_DoesNotExposeProviderBodyOrCallbackValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.None,
            OAuthSubjectSource.TokenResponse,
            cancellationToken
        );
        const string leaked = "provider-leaked-secret";
        scope.Handler.EnqueueJson(HttpStatusCode.BadRequest, $$"""{"error":"bad","error_description":"{{leaked}}"}""");
        var state = await StartAndReadStateAsync(scope, cancellationToken);

        var result = await scope.Authorization.HandleCallbackAsync(
            state,
            "callback-code-secret",
            null,
            "tester",
            cancellationToken
        );

        Assert.False(result.Success);
        Assert.DoesNotContain(leaked, result.RedirectPath, StringComparison.Ordinal);
        Assert.DoesNotContain("callback-code-secret", result.RedirectPath, StringComparison.Ordinal);
        Assert.DoesNotContain(leaked, string.Join('\n', scope.Logger.Messages), StringComparison.Ordinal);
        var connection = await scope.DbContext.Connections.SingleAsync(cancellationToken);
        Assert.Equal(ConnectionStatus.Invalid, connection.Status);
        Assert.Equal("integration.oauth_token_exchange_failed", connection.LastValidationErrorCode);
    }

    [Fact]
    public async Task Callback_ProviderError_UsesStableRedirectAndDoesNotReflectDescription()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.None,
            OAuthSubjectSource.TokenResponse,
            cancellationToken
        );
        var state = await StartAndReadStateAsync(scope, cancellationToken);

        var result = await scope.Authorization.HandleCallbackAsync(
            state,
            null,
            "access_denied:secret-provider-description",
            "tester",
            cancellationToken
        );

        Assert.False(result.Success);
        Assert.Contains("code=authorization_denied", result.RedirectPath, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-provider-description", result.RedirectPath, StringComparison.Ordinal);
        Assert.Empty(scope.Handler.Requests);
        var connection = await scope.DbContext.Connections.SingleAsync(cancellationToken);
        Assert.Equal(ConnectionStatus.PendingAuthorization, connection.Status);
    }

    private static async Task<string> StartAndReadStateAsync(OAuthTestScope scope, CancellationToken cancellationToken)
    {
        var started = await scope.Authorization.StartAsync(
            scope.ConnectionId,
            CallbackUri,
            "/integrations",
            OAuthCompletionTarget.Web,
            "tester",
            cancellationToken
        );
        return QueryHelpers.ParseQuery(new Uri(started.AuthorizationUrl).Query)["state"].ToString();
    }

    private static void AssertProtected(OAuthTestScope scope, ConnectionCredential credential, string expectedValue)
    {
        Assert.Equal(expectedValue, credential.Value);
    }

    private sealed class OAuthTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private OAuthTestScope(
            SqliteConnection connection,
            AgwDbContext dbContext,
            Guid connectionId,
            DateTimeOffset now,
            IConnectionCredentialReader reader,
            OAuthAuthorizationAppService authorization,
            OAuthRefreshAppService refresh,
            QueueHttpMessageHandler handler,
            ListLogger<OAuthAuthorizationAppService> logger
        )
        {
            _connection = connection;
            DbContext = dbContext;
            ConnectionId = connectionId;
            Now = now;
            Reader = reader;
            Authorization = authorization;
            Refresh = refresh;
            Handler = handler;
            Logger = logger;
        }

        public AgwDbContext DbContext { get; }
        public Guid ConnectionId { get; }
        public DateTimeOffset Now { get; }
        public IConnectionCredentialReader Reader { get; }
        public OAuthAuthorizationAppService Authorization { get; }
        public OAuthRefreshAppService Refresh { get; }
        public QueueHttpMessageHandler Handler { get; }
        public ListLogger<OAuthAuthorizationAppService> Logger { get; }

        public static async Task<OAuthTestScope> CreateAsync(
            OAuth2ClientAuthenticationMethod clientAuthenticationMethod,
            OAuthSubjectSource subjectSource,
            CancellationToken cancellationToken,
            bool supportsRefresh = false,
            string clientId = "oauth-client",
            string clientSecret = "oauth-secret"
        )
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var encryptedDataProtector = new DataProtectionEncryptedDataProtector(
                new EphemeralDataProtectionProvider()
            );
            var dbContext = new AgwDbContext(options, encryptedDataProtector);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
            var timeProvider = new TestTimeProvider(now);
            var catalog = new OAuthTestCatalog(clientAuthenticationMethod, subjectSource, supportsRefresh);
            var handler = new QueueHttpMessageHandler();
            var httpClientFactory = new TestHttpClientFactory(handler);
            var logger = new ListLogger<OAuthAuthorizationAppService>();

            IRepository<PluginInstallation> installationRepository = new EfRepository<PluginInstallation>(dbContext);
            IRepository<PluginInstallationCredential> installationCredentialRepository =
                new EfRepository<PluginInstallationCredential>(dbContext);
            IRepository<IntegrationConnection> connectionRepository = new EfRepository<IntegrationConnection>(
                dbContext
            );
            IRepository<ConnectionCredential> connectionCredentialRepository = new EfRepository<ConnectionCredential>(
                dbContext
            );
            IUnitOfWork unitOfWork = dbContext;
            var reader = new ConnectionCredentialReader(
                installationCredentialRepository,
                connectionCredentialRepository
            );
            var stateProtector = new OAuthStateProtector(new EphemeralDataProtectionProvider(), timeProvider);
            var authorization = new OAuthAuthorizationAppService(
                connectionRepository,
                installationRepository,
                connectionCredentialRepository,
                unitOfWork,
                catalog,
                reader,
                httpClientFactory,
                stateProtector,
                timeProvider,
                logger
            );
            var refresh = new OAuthRefreshAppService(authorization);

            var installation = new PluginInstallation
            {
                Id = Guid.CreateVersion7(),
                PluginId = OAuthTestCatalog.PluginId,
                Enabled = true,
                ConfigurationJson = IntegrationConfigurationCodec.Write(
                    new Dictionary<string, string?>
                    {
                        [
                            IntegrationConfigurationCodec.InstallationKey(
                                OAuthTestCatalog.ConnectorId,
                                OAuthTestCatalog.AuthSchemeId,
                                "client-id"
                            )
                        ] = clientId,
                    }
                ),
                CreateBy = "seed",
                CreateTime = now,
            };
            dbContext.PluginInstallations.Add(installation);
            if (
                clientAuthenticationMethod
                is OAuth2ClientAuthenticationMethod.Body
                    or OAuth2ClientAuthenticationMethod.Basic
            )
            {
                dbContext.PluginInstallationCredentials.Add(
                    new PluginInstallationCredential
                    {
                        Id = Guid.CreateVersion7(),
                        PluginInstallationId = installation.Id,
                        Slot = IntegrationCredentialSlots.InstallationField(
                            OAuthTestCatalog.ConnectorId,
                            OAuthTestCatalog.AuthSchemeId,
                            "client-secret"
                        ),
                        Value = clientSecret,
                        CreateBy = "seed",
                        CreateTime = now,
                    }
                );
            }

            var connectionId = Guid.CreateVersion7();
            dbContext.Connections.Add(
                new IntegrationConnection
                {
                    Id = connectionId,
                    PluginId = OAuthTestCatalog.PluginId,
                    ConnectorId = OAuthTestCatalog.ConnectorId,
                    AuthSchemeId = OAuthTestCatalog.AuthSchemeId,
                    DisplayName = "OAuth test",
                    Alias = $"oauth-{Guid.CreateVersion7():N}",
                    Enabled = true,
                    Status = ConnectionStatus.Unverified,
                    CreateBy = "seed",
                    CreateTime = now,
                }
            );
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();

            return new OAuthTestScope(
                connection,
                dbContext,
                connectionId,
                now,
                reader,
                authorization,
                refresh,
                handler,
                logger
            );
        }

        public async Task SeedConnectionCredentialAsync(
            string slot,
            string value,
            DateTimeOffset? expiresAtUtc,
            CancellationToken cancellationToken
        )
        {
            DbContext.ConnectionCredentials.Add(
                new ConnectionCredential
                {
                    Id = Guid.CreateVersion7(),
                    ConnectionId = ConnectionId,
                    Slot = slot,
                    Value = value,
                    ExpiresAtUtc = expiresAtUtc,
                    CreateBy = "seed",
                    CreateTime = Now,
                }
            );
            await DbContext.SaveChangesAsync(cancellationToken);
            DbContext.ChangeTracker.Clear();
        }

        public async Task<Guid> SeedOtherConnectionAsync(CancellationToken cancellationToken)
        {
            var connectionId = Guid.CreateVersion7();
            DbContext.Connections.Add(
                new IntegrationConnection
                {
                    Id = connectionId,
                    PluginId = OAuthTestCatalog.PluginId,
                    ConnectorId = OAuthTestCatalog.ConnectorId,
                    AuthSchemeId = OAuthTestCatalog.AuthSchemeId,
                    DisplayName = "Other OAuth test",
                    Alias = $"oauth-{Guid.CreateVersion7():N}",
                    Enabled = true,
                    Status = ConnectionStatus.Unverified,
                    CreateBy = "seed",
                    CreateTime = Now,
                }
            );
            await DbContext.SaveChangesAsync(cancellationToken);
            DbContext.ChangeTracker.Clear();
            return connectionId;
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class OAuthTestCatalog : IPluginCatalog
    {
        public const string PluginId = "oauth-test";
        public const string ConnectorId = "cloud";
        public const string AuthSchemeId = "oauth2";

        private readonly PluginDefinition _plugin;

        public OAuthTestCatalog(
            OAuth2ClientAuthenticationMethod clientAuthenticationMethod,
            OAuthSubjectSource subjectSource,
            bool supportsRefresh
        )
        {
            var installationFields = new List<FormFieldDefinition> { Field("client-id", FormFieldType.Text) };
            if (
                clientAuthenticationMethod
                is OAuth2ClientAuthenticationMethod.Body
                    or OAuth2ClientAuthenticationMethod.Basic
            )
            {
                installationFields.Add(Field("client-secret", FormFieldType.Secret));
            }

            _plugin = new PluginDefinition
            {
                Id = PluginId,
                Version = "1.0.0",
                DisplayName = "OAuth Test",
                Connectors =
                [
                    new ConnectorDefinition
                    {
                        Id = ConnectorId,
                        DisplayName = "Cloud",
                        AuthSchemes =
                        [
                            new AuthSchemeDefinition
                            {
                                Id = AuthSchemeId,
                                DisplayName = "OAuth 2.0",
                                Type = AuthSchemeType.OAuth2,
                                InstallationFields = installationFields,
                                OAuth2AuthorizationCode = new OAuth2AuthorizationCodeSettings
                                {
                                    AuthorizationEndpoint = "https://provider.test/authorize",
                                    TokenEndpoint = "https://provider.test/token",
                                    UserInfoEndpoint =
                                        subjectSource == OAuthSubjectSource.UserInfo
                                            ? "https://provider.test/userinfo"
                                            : null,
                                    ClientIdFieldId = "client-id",
                                    ClientSecretFieldId = clientAuthenticationMethod
                                        is OAuth2ClientAuthenticationMethod.Body
                                            or OAuth2ClientAuthenticationMethod.Basic
                                        ? "client-secret"
                                        : null,
                                    SubjectResolution = new OAuthSubjectResolutionDefinition
                                    {
                                        Source = subjectSource,
                                        Field = "account.name",
                                    },
                                    UsePkce = true,
                                    ClientAuthenticationMethod = clientAuthenticationMethod,
                                    SupportsRefresh = supportsRefresh,
                                    Scopes = ["repo", "read:user"],
                                    AdditionalAuthorizeParameters = new Dictionary<string, string>
                                    {
                                        ["prompt"] = "fixed",
                                    },
                                    AdditionalTokenParameters = new Dictionary<string, string>
                                    {
                                        ["audience"] = "agw",
                                        ["client_id"] = "must-not-win",
                                        ["client_secret"] = "must-not-be-sent",
                                    },
                                },
                            },
                        ],
                    },
                ],
            };
            PluginCatalogValidator.Validate([_plugin]);
        }

        public IReadOnlyList<PluginDefinition> List() => [_plugin];

        public PluginDefinition? Find(string pluginId) =>
            string.Equals(pluginId, PluginId, StringComparison.OrdinalIgnoreCase) ? _plugin : null;

        private static FormFieldDefinition Field(string id, FormFieldType type) =>
            new()
            {
                Id = id,
                Label = id,
                Type = type,
                IsRequired = true,
            };
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public TestHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<CapturedRequest> Requests { get; } = [];

        public void EnqueueJson(HttpStatusCode statusCode, string content)
        {
            _responses.Enqueue(
                new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json"),
                }
            );
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(
                new CapturedRequest
                {
                    Method = request.Method,
                    Uri = request.RequestUri!,
                    Authorization = request.Headers.Authorization,
                    UserAgent = request.Headers.UserAgent.ToString(),
                    Body =
                        request.Content == null
                            ? string.Empty
                            : await request.Content.ReadAsStringAsync(cancellationToken),
                }
            );
            return _responses.Dequeue();
        }
    }

    private sealed class CapturedRequest
    {
        public required HttpMethod Method { get; init; }
        public required Uri Uri { get; init; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; init; }
        public required string UserAgent { get; init; }
        public required string Body { get; init; }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
