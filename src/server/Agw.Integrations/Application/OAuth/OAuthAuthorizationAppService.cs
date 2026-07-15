using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Application.Management;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Contracts.OAuth;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

using IntegrationConnection = Agw.Shared.Data.Entities.Integrations.Connection;

namespace Agw.Integrations.Application.OAuth;

public sealed class OAuthAuthorizationAppService
{
    private const string PendingAuthorizationCode = "integration.pending_authorization";
    private const string AuthorizationDeniedCode = "integration.oauth_authorization_denied";
    private const string TokenExchangeFailedCode = "integration.oauth_token_exchange_failed";
    private const string RefreshFailedCode = "integration.oauth_refresh_failed";
    private const string InvalidStateRedirectCode = "invalid_state";
    private const string AuthorizationDeniedRedirectCode = "authorization_denied";
    private const string TokenExchangeFailedRedirectCode = "token_exchange_failed";
    private const string SuccessRedirectCode = "authorized";
    private static readonly string[] ReservedTokenParameters =
    [
        "grant_type",
        "code",
        "redirect_uri",
        "client_id",
        "client_secret",
        "code_verifier",
        "refresh_token"
    ];
    private static readonly string[] ReservedAuthorizeParameters =
    [
        "client_id",
        "response_type",
        "redirect_uri",
        "state",
        "scope",
        "code_challenge",
        "code_challenge_method"
    ];

    private readonly IRepository<IntegrationConnection> _connectionRepository;
    private readonly IRepository<PluginInstallation> _installationRepository;
    private readonly IRepository<ConnectionCredential> _connectionCredentialRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPluginCatalog _pluginCatalog;
    private readonly IConnectionCredentialReader _credentialReader;
    private readonly IConnectionCredentialProtector _credentialProtector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OAuthStateProtector _stateProtector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OAuthAuthorizationAppService> _logger;

    public OAuthAuthorizationAppService(
        IRepository<IntegrationConnection> connectionRepository,
        IRepository<PluginInstallation> installationRepository,
        IRepository<ConnectionCredential> connectionCredentialRepository,
        IUnitOfWork unitOfWork,
        IPluginCatalog pluginCatalog,
        IConnectionCredentialReader credentialReader,
        IConnectionCredentialProtector credentialProtector,
        IHttpClientFactory httpClientFactory,
        OAuthStateProtector stateProtector,
        TimeProvider timeProvider,
        ILogger<OAuthAuthorizationAppService> logger)
    {
        _connectionRepository = connectionRepository;
        _installationRepository = installationRepository;
        _connectionCredentialRepository = connectionCredentialRepository;
        _unitOfWork = unitOfWork;
        _pluginCatalog = pluginCatalog;
        _credentialReader = credentialReader;
        _credentialProtector = credentialProtector;
        _httpClientFactory = httpClientFactory;
        _stateProtector = stateProtector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<OAuthAuthorizeStartResponse> StartAsync(
        Guid connectionId,
        string callbackUri,
        string returnPath,
        string user,
        CancellationToken cancellationToken)
    {
        OAuthStateProtector.ValidateReturnPath(returnPath);
        ValidateCallbackUri(callbackUri);
        var context = await ResolveContextAsync(connectionId, cancellationToken);
        var verifier = context.Settings.UsePkce ? CreatePkceVerifier() : null;
        var state = _stateProtector.Protect(connectionId, verifier, returnPath);
        var parameters = new Dictionary<string, string?>(
            context.Settings.AdditionalAuthorizeParameters.ToDictionary(
                item => item.Key,
                item => (string?)item.Value),
            StringComparer.Ordinal);
        foreach (var reservedParameter in ReservedAuthorizeParameters)
        {
            parameters.Remove(reservedParameter);
        }

        parameters.Add("client_id", context.ClientId);
        parameters.Add("response_type", "code");
        parameters.Add("redirect_uri", callbackUri);
        parameters.Add("state", state);
        if (context.Settings.Scopes.Count > 0)
        {
            parameters["scope"] = string.Join(' ', context.Settings.Scopes);
        }

        if (verifier != null)
        {
            parameters["code_challenge"] = WebEncoders.Base64UrlEncode(
                SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            parameters["code_challenge_method"] = "S256";
        }

        context.Connection.Status = ConnectionStatus.PendingAuthorization;
        context.Connection.LastValidationErrorCode = PendingAuthorizationCode;
        context.Connection.LastValidatedAtUtc = null;
        context.Connection.UpdateBy = user;
        context.Connection.UpdateTime = _timeProvider.GetUtcNow();
        await _unitOfWork.SaveChangesAsync();

        return new OAuthAuthorizeStartResponse
        {
            AuthorizationUrl = QueryHelpers.AddQueryString(context.Settings.AuthorizationEndpoint, parameters)
        };
    }

    public async Task<OAuthCallbackResult> HandleCallbackAsync(
        string? protectedState,
        string? authorizationCode,
        string? providerError,
        string callbackUri,
        string user,
        CancellationToken cancellationToken)
    {
        ValidateCallbackUri(callbackUri);
        if (!_stateProtector.TryUnprotect(protectedState, out var state) || state == null)
        {
            return FailedRedirect("/integrations", InvalidStateRedirectCode);
        }

        OAuthConnectionContext context;
        try
        {
            context = await ResolveContextAsync(state.ConnectionId, cancellationToken);
        }
        catch (AgwException)
        {
            return FailedRedirect(state.ReturnPath, InvalidStateRedirectCode);
        }

        if (!string.IsNullOrWhiteSpace(providerError) || string.IsNullOrWhiteSpace(authorizationCode))
        {
            await SetFailureAsync(
                context.Connection,
                ConnectionStatus.PendingAuthorization,
                AuthorizationDeniedCode,
                user);
            return FailedRedirect(state.ReturnPath, AuthorizationDeniedRedirectCode);
        }

        try
        {
            var form = BuildTokenForm(context, callbackUri);
            form["grant_type"] = "authorization_code";
            form["code"] = authorizationCode;
            if (!string.IsNullOrWhiteSpace(state.PkceVerifier))
            {
                form["code_verifier"] = state.PkceVerifier;
            }

            var token = await RequestTokenAsync(context, form, cancellationToken);
            var subject = await ResolveSubjectAsync(context, token, required: true, cancellationToken);
            await SaveTokensAsync(context.Connection, token, preserveMissingRefreshToken: false, user);
            context.Connection.Subject = subject;
            MarkReady(context.Connection, user);
            await _unitOfWork.SaveChangesAsync();
            return SucceededRedirect(state.ReturnPath);
        }
        catch (Exception exception) when (IsProviderFailure(exception, cancellationToken))
        {
            _logger.LogWarning(
                "OAuth authorization failed for connection {ConnectionId}.",
                context.Connection.Id);
            await SetFailureAsync(
                context.Connection,
                ConnectionStatus.Invalid,
                TokenExchangeFailedCode,
                user);
            return FailedRedirect(state.ReturnPath, TokenExchangeFailedRedirectCode);
        }
    }

    internal async Task<OAuthRefreshResponse> RefreshAsync(
        Guid connectionId,
        string user,
        CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(connectionId, cancellationToken);
        if (!context.Settings.SupportsRefresh)
        {
            throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
        }

        var refreshCredential = await _credentialReader.ReadConnectionAsync(
            connectionId,
            IntegrationCredentialSlots.OAuthRefreshToken,
            cancellationToken);
        if (refreshCredential == null)
        {
            throw new AgwException(ErrorCodes.IntegrationCredentialUnavailable);
        }

        try
        {
            var form = BuildTokenForm(context, callbackUri: null);
            form["grant_type"] = "refresh_token";
            form["refresh_token"] = refreshCredential.Value;
            var token = await RequestTokenAsync(context, form, cancellationToken);
            var subject = await ResolveSubjectAsync(context, token, required: false, cancellationToken);
            await SaveTokensAsync(context.Connection, token, preserveMissingRefreshToken: true, user);
            if (!string.IsNullOrWhiteSpace(subject))
            {
                context.Connection.Subject = subject;
            }
            MarkReady(context.Connection, user);
            await _unitOfWork.SaveChangesAsync();
            return new OAuthRefreshResponse
            {
                ConnectionId = connectionId,
                ExpiresAtUtc = token.ExpiresAtUtc
            };
        }
        catch (Exception exception) when (IsProviderFailure(exception, cancellationToken))
        {
            _logger.LogWarning("OAuth refresh failed for connection {ConnectionId}.", connectionId);
            await SetFailureAsync(context.Connection, ConnectionStatus.Invalid, RefreshFailedCode, user);
            throw new AgwException(ErrorCodes.OAuthProviderRequestFailed);
        }
    }

    private async Task<OAuthConnectionContext> ResolveContextAsync(
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var connection = await _connectionRepository.Queryable
            .Include(item => item.Credentials)
            .FirstOrDefaultAsync(item => item.Id == connectionId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ConnectionNotFound);
        if (!connection.Enabled)
        {
            throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
        }

        var definition = IntegrationDefinitionResolver.Resolve(
            _pluginCatalog,
            connection.PluginId,
            connection.ConnectorId,
            connection.AuthSchemeId);
        if (definition.AuthScheme.Type != AuthSchemeType.OAuth2
            || definition.AuthScheme.OAuth2AuthorizationCode == null)
        {
            throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
        }

        var installation = await _installationRepository.Queryable
            .Include(item => item.Credentials)
            .FirstOrDefaultAsync(
                item => item.PluginId == connection.PluginId && item.Enabled,
                cancellationToken)
            ?? throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
        var settings = definition.AuthScheme.OAuth2AuthorizationCode;
        var clientId = await ReadInstallationFieldAsync(
            installation,
            definition,
            settings.ClientIdFieldId,
            cancellationToken);
        string? clientSecret = null;
        if (settings.ClientAuthenticationMethod is OAuth2ClientAuthenticationMethod.Basic
            or OAuth2ClientAuthenticationMethod.Body)
        {
            clientSecret = await ReadInstallationFieldAsync(
                installation,
                definition,
                settings.ClientSecretFieldId!,
                cancellationToken);
        }

        return new OAuthConnectionContext(
            connection,
            installation,
            definition,
            settings,
            clientId,
            clientSecret);
    }

    private async Task<string> ReadInstallationFieldAsync(
        PluginInstallation installation,
        ResolvedIntegrationDefinition definition,
        string fieldId,
        CancellationToken cancellationToken)
    {
        var field = definition.AuthScheme.InstallationFields.First(item => string.Equals(
            item.Id,
            fieldId,
            StringComparison.OrdinalIgnoreCase));
        string? value;
        if (field.Type == FormFieldType.Secret)
        {
            var credential = await _credentialReader.ReadPluginInstallationAsync(
                installation.Id,
                IntegrationCredentialSlots.InstallationField(
                    definition.Connector.Id,
                    definition.AuthScheme.Id,
                    fieldId),
                cancellationToken);
            value = credential?.Value;
        }
        else
        {
            var configuration = IntegrationConfigurationCodec.Read(installation.ConfigurationJson);
            configuration.TryGetValue(
                IntegrationConfigurationCodec.InstallationKey(
                    definition.Connector.Id,
                    definition.AuthScheme.Id,
                    fieldId),
                out value);
        }

        return string.IsNullOrWhiteSpace(value)
            ? throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid)
            : value;
    }

    private Dictionary<string, string> BuildTokenForm(
        OAuthConnectionContext context,
        string? callbackUri)
    {
        var form = new Dictionary<string, string>(
            context.Settings.AdditionalTokenParameters,
            StringComparer.Ordinal);
        foreach (var reservedParameter in ReservedTokenParameters)
        {
            form.Remove(reservedParameter);
        }

        if (callbackUri != null)
        {
            form["redirect_uri"] = callbackUri;
        }

        switch (context.Settings.ClientAuthenticationMethod)
        {
            case OAuth2ClientAuthenticationMethod.Body:
                form["client_id"] = context.ClientId;
                form["client_secret"] = context.ClientSecret!;
                break;
            case OAuth2ClientAuthenticationMethod.None:
                form["client_id"] = context.ClientId;
                break;
            case OAuth2ClientAuthenticationMethod.Basic:
                break;
            default:
                throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
        }

        return form;
    }

    private async Task<OAuthTokenPayload> RequestTokenAsync(
        OAuthConnectionContext context,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, context.Settings.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (context.Settings.ClientAuthenticationMethod == OAuth2ClientAuthenticationMethod.Basic)
        {
            var encodedClientId = WebUtility.UrlEncode(context.ClientId);
            var encodedClientSecret = WebUtility.UrlEncode(context.ClientSecret);
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{encodedClientId}:{encodedClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", value);
        }

        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw OAuthProtocolFailure();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var accessToken = ReadRequiredString(root, "access_token");
        var refreshToken = ReadOptionalString(root, "refresh_token");
        var idToken = ReadOptionalString(root, "id_token");
        var expiresAtUtc = ReadExpiresAt(root, _timeProvider.GetUtcNow());
        return new OAuthTokenPayload(
            accessToken,
            refreshToken,
            idToken,
            expiresAtUtc,
            root.Clone());
    }

    private async Task<string?> ResolveSubjectAsync(
        OAuthConnectionContext context,
        OAuthTokenPayload token,
        bool required,
        CancellationToken cancellationToken)
    {
        JsonElement source;
        switch (context.Settings.SubjectResolution.Source)
        {
            case OAuthSubjectSource.TokenResponse:
                source = token.Response;
                break;
            case OAuthSubjectSource.IdToken:
                if (string.IsNullOrWhiteSpace(token.IdToken))
                {
                    return RequiredSubject(required);
                }
                source = ParseJwtPayload(token.IdToken);
                break;
            case OAuthSubjectSource.UserInfo:
                source = await RequestUserInfoAsync(context.Settings.UserInfoEndpoint!, token.AccessToken, cancellationToken);
                break;
            default:
                throw OAuthProtocolFailure();
        }

        return TryReadJsonPath(source, context.Settings.SubjectResolution.Field, out var value)
            ? value
            : RequiredSubject(required);
    }

    private async Task<JsonElement> RequestUserInfoAsync(
        string endpoint,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Agw/1.0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw OAuthProtocolFailure();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private async Task SaveTokensAsync(
        IntegrationConnection connection,
        OAuthTokenPayload token,
        bool preserveMissingRefreshToken,
        string user)
    {
        await UpsertCredentialAsync(
            connection,
            IntegrationCredentialSlots.OAuthAccessToken,
            token.AccessToken,
            token.ExpiresAtUtc,
            user);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            await UpsertCredentialAsync(
                connection,
                IntegrationCredentialSlots.OAuthRefreshToken,
                token.RefreshToken,
                null,
                user);
        }
        else if (!preserveMissingRefreshToken)
        {
            RemoveCredential(connection, IntegrationCredentialSlots.OAuthRefreshToken);
        }

        if (!string.IsNullOrWhiteSpace(token.IdToken))
        {
            await UpsertCredentialAsync(
                connection,
                IntegrationCredentialSlots.OAuthIdToken,
                token.IdToken,
                null,
                user);
        }
        else if (!preserveMissingRefreshToken)
        {
            RemoveCredential(connection, IntegrationCredentialSlots.OAuthIdToken);
        }
    }

    private async Task UpsertCredentialAsync(
        IntegrationConnection connection,
        string slot,
        string value,
        DateTimeOffset? expiresAtUtc,
        string user)
    {
        var credential = connection.Credentials.FirstOrDefault(item => string.Equals(
            item.Slot,
            slot,
            StringComparison.OrdinalIgnoreCase));
        if (credential == null)
        {
            credential = new ConnectionCredential
            {
                Id = Guid.NewGuid(),
                ConnectionId = connection.Id,
                Connection = connection,
                Slot = slot,
                CreateBy = user,
                CreateTime = _timeProvider.GetUtcNow()
            };
            connection.Credentials.Add(credential);
            await _connectionCredentialRepository.AddAsync(credential);
        }
        else
        {
            credential.UpdateBy = user;
            credential.UpdateTime = _timeProvider.GetUtcNow();
        }

        credential.ProtectedValue = _credentialProtector.Protect(value);
        credential.ExpiresAtUtc = expiresAtUtc;
        credential.MetadataJson = null;
        credential.FormatVersion = 1;
    }

    private void RemoveCredential(IntegrationConnection connection, string slot)
    {
        var credential = connection.Credentials.FirstOrDefault(item => string.Equals(
            item.Slot,
            slot,
            StringComparison.OrdinalIgnoreCase));
        if (credential == null)
        {
            return;
        }

        _connectionCredentialRepository.Remove(credential);
        connection.Credentials.Remove(credential);
    }

    private void MarkReady(IntegrationConnection connection, string user)
    {
        var now = _timeProvider.GetUtcNow();
        connection.Status = ConnectionStatus.Ready;
        connection.LastValidatedAtUtc = now;
        connection.LastValidationErrorCode = null;
        connection.UpdateBy = user;
        connection.UpdateTime = now;
    }

    private async Task SetFailureAsync(
        IntegrationConnection connection,
        ConnectionStatus status,
        string errorCode,
        string user)
    {
        connection.Status = status;
        connection.LastValidatedAtUtc = null;
        connection.LastValidationErrorCode = errorCode;
        connection.UpdateBy = user;
        connection.UpdateTime = _timeProvider.GetUtcNow();
        await _unitOfWork.SaveChangesAsync();
    }

    private static string CreatePkceVerifier()
    {
        return WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }

    private static void ValidateCallbackUri(string callbackUri)
    {
        if (!Uri.TryCreate(callbackUri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
        }
    }

    private static OAuthCallbackResult SucceededRedirect(string returnPath)
    {
        return new OAuthCallbackResult
        {
            Success = true,
            RedirectPath = QueryHelpers.AddQueryString(returnPath, "oauth", SuccessRedirectCode)
        };
    }

    private static OAuthCallbackResult FailedRedirect(string returnPath, string code)
    {
        return new OAuthCallbackResult
        {
            Success = false,
            RedirectPath = QueryHelpers.AddQueryString(
                returnPath,
                new Dictionary<string, string?>
                {
                    ["oauth"] = "error",
                    ["code"] = code
                })
        };
    }

    private static bool IsProviderFailure(Exception exception, CancellationToken cancellationToken)
    {
        return exception is AgwException agwException
                && agwException.Code == ErrorCodes.OAuthProviderRequestFailed.Code
            || exception is HttpRequestException or JsonException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        return ReadOptionalString(element, propertyName) is { Length: > 0 } value
            ? value
            : throw OAuthProtocolFailure();
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static DateTimeOffset? ReadExpiresAt(JsonElement element, DateTimeOffset now)
    {
        if (!TryGetProperty(element, "expires_in", out var expiresElement))
        {
            return null;
        }

        long seconds;
        if (expiresElement.ValueKind == JsonValueKind.Number)
        {
            if (!expiresElement.TryGetInt64(out seconds))
            {
                throw OAuthProtocolFailure();
            }
        }
        else if (expiresElement.ValueKind == JsonValueKind.String
            && long.TryParse(expiresElement.GetString(), out var parsed))
        {
            seconds = parsed;
        }
        else
        {
            throw OAuthProtocolFailure();
        }

        if (seconds < 0 || seconds > 315_360_000)
        {
            throw OAuthProtocolFailure();
        }

        return now.AddSeconds(seconds);
    }

    private static JsonElement ParseJwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw OAuthProtocolFailure();
        }

        try
        {
            var json = WebEncoders.Base64UrlDecode(parts[1]);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw OAuthProtocolFailure();
        }
    }

    private static bool TryReadJsonPath(JsonElement root, string path, out string? value)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !TryGetProperty(current, segment, out current))
            {
                value = null;
                return false;
            }
        }

        value = current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? RequiredSubject(bool required)
    {
        return required ? throw OAuthProtocolFailure() : null;
    }

    private sealed class OAuthConnectionContext
    {
        public OAuthConnectionContext(
            IntegrationConnection connection,
            PluginInstallation installation,
            ResolvedIntegrationDefinition definition,
            OAuth2AuthorizationCodeSettings settings,
            string clientId,
            string? clientSecret)
        {
            Connection = connection;
            Installation = installation;
            Definition = definition;
            Settings = settings;
            ClientId = clientId;
            ClientSecret = clientSecret;
        }

        public IntegrationConnection Connection { get; }
        public PluginInstallation Installation { get; }
        public ResolvedIntegrationDefinition Definition { get; }
        public OAuth2AuthorizationCodeSettings Settings { get; }
        public string ClientId { get; }
        public string? ClientSecret { get; }
    }

    private sealed class OAuthTokenPayload
    {
        public OAuthTokenPayload(
            string accessToken,
            string? refreshToken,
            string? idToken,
            DateTimeOffset? expiresAtUtc,
            JsonElement response)
        {
            AccessToken = accessToken;
            RefreshToken = refreshToken;
            IdToken = idToken;
            ExpiresAtUtc = expiresAtUtc;
            Response = response;
        }

        public string AccessToken { get; }
        public string? RefreshToken { get; }
        public string? IdToken { get; }
        public DateTimeOffset? ExpiresAtUtc { get; }
        public JsonElement Response { get; }
    }

    private static AgwException OAuthProtocolFailure()
    {
        return new AgwException(ErrorCodes.OAuthProviderRequestFailed);
    }
}
