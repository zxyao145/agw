using System.Net;
using System.Security.Claims;
using System.Text;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Encryption;
using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Application.Management;
using Agw.Integrations.Application.OAuth;
using Agw.Integrations.Application.Persistence;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Contracts.OAuth;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Data.Entities.Integrations;
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
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
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

    [Fact]
    public async Task StartAsync_ForeignConnection_ThrowsConnectionNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.None,
            OAuthSubjectSource.TokenResponse,
            cancellationToken
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Authorization.StartAsync(
                scope.ConnectionId,
                CallbackUri,
                "/integrations",
                OAuthCompletionTarget.Web,
                "other-user",
                cancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ConnectionNotFound.Code, exception.Code);
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
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
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
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
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
        Assert.Equal("tester", connection.UpdateBy);
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
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.Basic,
            OAuthSubjectSource.TokenResponse,
            cancellationToken
        );
        scope.Handler.EnqueueJson(HttpStatusCode.OK, "{\"access_token\":\"token\",\"account\":{\"name\":\"user\"}}");
        var state = await StartAndReadStateAsync(scope, cancellationToken);

        await scope.Authorization.HandleCallbackAsync(state, "code", null, cancellationToken);

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
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
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

        await scope.Authorization.HandleCallbackAsync(state, "code", null, cancellationToken);

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
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.None,
            source,
            cancellationToken
        );
        scope.Handler.EnqueueJson(HttpStatusCode.OK, tokenResponse);
        var state = await StartAndReadStateAsync(scope, cancellationToken);

        var result = await scope.Authorization.HandleCallbackAsync(state, "code", null, cancellationToken);

        Assert.True(result.Success);
        var connection = await scope.DbContext.Connections.SingleAsync(cancellationToken);
        Assert.Equal(expectedSubject, connection.Subject);
    }

    [Fact]
    public async Task Callback_UserInfoSubject_UsesNewAccessToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.None,
            OAuthSubjectSource.UserInfo,
            cancellationToken
        );
        scope.Handler.EnqueueJson(HttpStatusCode.OK, "{\"access_token\":\"userinfo-token\"}");
        scope.Handler.EnqueueJson(HttpStatusCode.OK, "{\"account\":{\"name\":\"userinfo-user\"}}");
        var state = await StartAndReadStateAsync(scope, cancellationToken);

        var result = await scope.Authorization.HandleCallbackAsync(state, "code", null, cancellationToken);

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
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
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
            "tester",
            IntegrationCredentialSlots.OAuthAccessToken,
            cancellationToken
        );
        var refresh = await scope.Reader.ReadConnectionAsync(
            scope.ConnectionId,
            "tester",
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
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
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
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
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
            cancellationToken
        );

        Assert.False(result.Success);
        Assert.Contains("code=authorization_denied", result.RedirectPath, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-provider-description", result.RedirectPath, StringComparison.Ordinal);
        Assert.Empty(scope.Handler.Requests);
        var connection = await scope.DbContext.Connections.SingleAsync(cancellationToken);
        Assert.Equal(ConnectionStatus.PendingAuthorization, connection.Status);
    }

    [Fact]
    public async Task Callback_PersistenceFailure_ReturnsFailedRedirectAndMarksConnectionInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
        await using var scope = await OAuthTestScope.CreateAsync(
            OAuth2ClientAuthenticationMethod.None,
            OAuthSubjectSource.TokenResponse,
            cancellationToken,
            failCallbackPersistence: true
        );
        scope.Handler.EnqueueJson(HttpStatusCode.OK, "{\"access_token\":\"token\",\"account\":{\"name\":\"user\"}}");
        var state = await StartAndReadStateAsync(scope, cancellationToken);

        var result = await scope.Authorization.HandleCallbackAsync(state, "code", null, cancellationToken);

        Assert.False(result.Success);
        Assert.Contains("code=token_exchange_failed", result.RedirectPath, StringComparison.Ordinal);
        scope.DbContext.ChangeTracker.Clear();
        var connection = await scope.DbContext.Connections.SingleAsync(cancellationToken);
        Assert.Equal(ConnectionStatus.Invalid, connection.Status);
        Assert.Equal("integration.oauth_token_exchange_failed", connection.LastValidationErrorCode);
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
            TestCredentialReader reader,
            TestOAuthAuthorizationClient authorization,
            TestOAuthRefreshClient refresh,
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
        public TestCredentialReader Reader { get; }
        public TestOAuthAuthorizationClient Authorization { get; }
        public TestOAuthRefreshClient Refresh { get; }
        public QueueHttpMessageHandler Handler { get; }
        public ListLogger<OAuthAuthorizationAppService> Logger { get; }

        public static async Task<OAuthTestScope> CreateAsync(
            OAuth2ClientAuthenticationMethod clientAuthenticationMethod,
            OAuthSubjectSource subjectSource,
            CancellationToken cancellationToken,
            bool supportsRefresh = false,
            bool failCallbackPersistence = false,
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

            IIntegrationsDbContext persistence = failCallbackPersistence
                ? new FailCallbackPersistenceUnitOfWork(dbContext)
                : dbContext;
            var userInfo = new TestUserInfoService();
            var reader = new ConnectionCredentialReader(persistence, userInfo);
            var stateProtector = new OAuthStateProtector(new EphemeralDataProtectionProvider(), timeProvider);
            var authorization = new OAuthAuthorizationAppService(
                persistence,
                catalog,
                reader,
                httpClientFactory,
                stateProtector,
                timeProvider,
                logger,
                userInfo
            );
            var refresh = new OAuthRefreshAppService(authorization);
            var credentialReader = new TestCredentialReader(reader, userInfo);
            var authorizationClient = new TestOAuthAuthorizationClient(authorization, userInfo);
            var refreshClient = new TestOAuthRefreshClient(refresh, userInfo);

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
                CreateBy = "tester",
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
                        CreateBy = "tester",
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
                    CreateBy = "tester",
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
                credentialReader,
                authorizationClient,
                refreshClient,
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
            using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
            DbContext.ConnectionCredentials.Add(
                new ConnectionCredential
                {
                    Id = Guid.CreateVersion7(),
                    ConnectionId = ConnectionId,
                    Slot = slot,
                    Value = value,
                    ExpiresAtUtc = expiresAtUtc,
                    CreateBy = "tester",
                    CreateTime = Now,
                }
            );
            await DbContext.SaveChangesAsync(cancellationToken);
            DbContext.ChangeTracker.Clear();
        }

        public async Task<Guid> SeedOtherConnectionAsync(CancellationToken cancellationToken)
        {
            using var systemScope = UserInfoUtil.PushSystemScope();
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
                    CreateBy = "other-user",
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

    private sealed class TestOAuthAuthorizationClient
    {
        private readonly OAuthAuthorizationAppService _service;
        private readonly TestUserInfoService _userInfo;

        public TestOAuthAuthorizationClient(OAuthAuthorizationAppService service, TestUserInfoService userInfo)
        {
            _service = service;
            _userInfo = userInfo;
        }

        public async Task<OAuthAuthorizeStartResponse> StartAsync(
            Guid connectionId,
            string callbackUri,
            string returnPath,
            OAuthCompletionTarget completionTarget,
            string userId,
            CancellationToken cancellationToken
        )
        {
            _userInfo.UserId = userId;
            using var userScope = UserInfoUtil.Push(CreatePrincipal(userId));
            return await _service
                .StartAsync(connectionId, callbackUri, returnPath, completionTarget, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<OAuthCallbackResult> HandleCallbackAsync(
            string? protectedState,
            string? authorizationCode,
            string? providerError,
            CancellationToken cancellationToken
        )
        {
            return _service.HandleCallbackAsync(protectedState, authorizationCode, providerError, cancellationToken);
        }
    }

    private sealed class TestOAuthRefreshClient
    {
        private readonly OAuthRefreshAppService _service;
        private readonly TestUserInfoService _userInfo;

        public TestOAuthRefreshClient(OAuthRefreshAppService service, TestUserInfoService userInfo)
        {
            _service = service;
            _userInfo = userInfo;
        }

        public async Task<OAuthRefreshResponse> RefreshAsync(
            Guid connectionId,
            string userId,
            CancellationToken cancellationToken
        )
        {
            _userInfo.UserId = userId;
            using var userScope = UserInfoUtil.Push(CreatePrincipal(userId));
            return await _service.RefreshAsync(connectionId, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TestCredentialReader
    {
        private readonly IConnectionCredentialReader _reader;
        private readonly TestUserInfoService _userInfo;

        public TestCredentialReader(IConnectionCredentialReader reader, TestUserInfoService userInfo)
        {
            _reader = reader;
            _userInfo = userInfo;
        }

        public async Task<ResolvedCredential?> ReadConnectionAsync(
            Guid connectionId,
            string userId,
            string slot,
            CancellationToken cancellationToken
        )
        {
            _userInfo.UserId = userId;
            using var userScope = UserInfoUtil.Push(CreatePrincipal(userId));
            return await _reader.ReadConnectionAsync(connectionId, slot, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ClaimsPrincipal CreatePrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private sealed class FailCallbackPersistenceUnitOfWork : IIntegrationsDbContext
    {
        private readonly AgwDbContext _inner;
        private int _saveCount;

        public FailCallbackPersistenceUnitOfWork(AgwDbContext inner)
        {
            _inner = inner;
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveCount) == 2)
            {
                throw new DbUpdateException("simulated callback persistence failure");
            }

            return _inner.SaveChangesAsync(cancellationToken);
        }

        public DbSet<PluginInstallation> PluginInstallations => _inner.PluginInstallations;
        public DbSet<PluginInstallationCredential> PluginInstallationCredentials =>
            _inner.PluginInstallationCredentials;
        public DbSet<IntegrationConnection> Connections => _inner.Connections;
        public DbSet<ConnectionCredential> ConnectionCredentials => _inner.ConnectionCredentials;
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
