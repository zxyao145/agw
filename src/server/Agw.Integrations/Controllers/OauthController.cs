using System.Globalization;
using System.Text.Json;

using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Repositories;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;


namespace Agw.Integrations.Controllers;

[ApiController]
[Route("api/integrations/oauth")]
public class OAuthController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRepository<AppDefinition> _appDefinitionRepository;
    private readonly IRepository<AppInstance> _appInstanceRepository;
    private readonly IRepository<OAuthAuthorizationToken> _oAuthAuthorizationTokenRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<OAuthController> _logger;
    private readonly TimeProvider _timeProvider;

    public OAuthController(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IRepository<AppDefinition> appDefinitionRepository,
        IRepository<AppInstance> appInstanceRepository,
        IRepository<OAuthAuthorizationToken> oAuthAuthorizationTokenRepository,
        IUnitOfWork unitOfWork,
        ILogger<OAuthController> logger,
        TimeProvider timeProvider)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _appDefinitionRepository = appDefinitionRepository;
        _appInstanceRepository = appInstanceRepository;
        _oAuthAuthorizationTokenRepository = oAuthAuthorizationTokenRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    [HttpGet("callback")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> OAuthCallback(CancellationToken cancellationToken)
    {
        var queryParameters = BuildRedirectQueryParameters();
        var callbackState = TryReadCallbackState();

        if (!string.IsNullOrWhiteSpace(callbackState?.State))
        {
            DeleteCallbackStateCookie(callbackState.State);
        }

        if (!string.IsNullOrWhiteSpace(Request.Query["code"]) && string.IsNullOrWhiteSpace(Request.Query["error"]))
        {
            var exchangeResult = await ExchangeAuthorizationCodeAsync(callbackState, cancellationToken);
            queryParameters.AddRange(exchangeResult.ToQueryParameters());
        }

        var uiCallbackUrl = ResolveUiCallbackUrl(callbackState);
        var redirectUrl = queryParameters.Count == 0
            ? uiCallbackUrl
            : QueryHelpers.AddQueryString(uiCallbackUrl, queryParameters);

        return Redirect(redirectUrl);
    }

    private List<KeyValuePair<string, string?>> BuildRedirectQueryParameters()
    {
        var queryParameters = new List<KeyValuePair<string, string?>>();

        foreach (var (key, values) in Request.Query)
        {
            foreach (var value in values)
            {
                queryParameters.Add(new KeyValuePair<string, string?>(key, value));
            }
        }

        return queryParameters;
    }

    private async Task<OAuthExchangeResult> ExchangeAuthorizationCodeAsync(
        OAuthCallbackState? callbackState,
        CancellationToken cancellationToken)
    {
        var authorizationCode = Request.Query["code"].ToString();
        if (string.IsNullOrWhiteSpace(authorizationCode))
        {
            return OAuthExchangeResult.Failed("missing_code");
        }

        var providerKey = Request.Query["provider"].ToString();
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            providerKey = callbackState?.IntegrationId ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return OAuthExchangeResult.Failed("missing_provider");
        }

        var providerConfiguration = await ResolveProviderConfigurationAsync(providerKey, callbackState, cancellationToken);
        if (providerConfiguration == null)
        {
            _logger.LogWarning("OAuth callback for provider {Provider} skipped because configuration is missing.", providerKey);
            return OAuthExchangeResult.Failed("provider_not_configured", providerKey);
        }

        if (providerConfiguration.AppInstanceId == Guid.Empty
            || string.IsNullOrWhiteSpace(providerConfiguration.ClientId)
            || string.IsNullOrWhiteSpace(providerConfiguration.TokenEndpoint))
        {
            _logger.LogWarning("OAuth callback for provider {Provider} skipped because configuration is incomplete.", providerKey);
            return OAuthExchangeResult.Failed("provider_configuration_incomplete", providerKey);
        }

        try
        {
            using var tokenResponse = await RequestAccessTokenAsync(
                authorizationCode,
                providerConfiguration,
                callbackState,
                cancellationToken);

            var responseContent = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("OAuth token exchange response for provider {Provider}: {ResponseContent}", providerKey, responseContent);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                var exchangeError = ExtractErrorDescription(responseContent) ?? $"token_exchange_failed_{(int)tokenResponse.StatusCode}";
                _logger.LogWarning(
                    "OAuth token exchange failed for provider {Provider}. Status: {StatusCode}. Body: {Body}",
                    providerKey,
                    (int)tokenResponse.StatusCode,
                    responseContent);
                return OAuthExchangeResult.Failed(exchangeError, providerKey);
            }

            using var document = JsonDocument.Parse(responseContent);
            var root = document.RootElement;
            var accessToken = TryGetString(root, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogWarning("OAuth token exchange for provider {Provider} succeeded but no access_token was returned.", providerKey);
                return OAuthExchangeResult.Failed("missing_access_token", providerKey);
            }

            var subject = ResolveSubject(root, providerConfiguration, callbackState);
            var now = _timeProvider.GetUtcNow();
            var tokenEntity = await _oAuthAuthorizationTokenRepository.Queryable
                .FirstOrDefaultAsync(
                    token => token.AppInstanceId == providerConfiguration.AppInstanceId,
                    cancellationToken);

            if (tokenEntity == null)
            {
                tokenEntity = new OAuthAuthorizationToken
                {
                    Id = Guid.NewGuid(),
                    AppInstanceId = providerConfiguration.AppInstanceId,
                    Subject = subject,
                };

                await _oAuthAuthorizationTokenRepository.AddAsync(tokenEntity);
            }
            else
            {
                _oAuthAuthorizationTokenRepository.Update(tokenEntity);
            }

            tokenEntity.Subject = subject;
            tokenEntity.AccessToken = accessToken;
            tokenEntity.RefreshToken = TryGetString(root, "refresh_token");
            tokenEntity.TokenType = TryGetString(root, "token_type") ?? "Bearer";
            //tokenEntity.Scope = TryGetString(root, "scope");
            tokenEntity.ExpiresAtUtc = ResolveExpiresAtUtc(root, now);

            await _unitOfWork.SaveChangesAsync();

            return OAuthExchangeResult.Succeeded(providerKey, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OAuth token exchange failed for provider {Provider}.", providerKey);
            return OAuthExchangeResult.Failed("token_exchange_exception", providerKey);
        }
    }

    private async Task<HttpResponseMessage> RequestAccessTokenAsync(
        string authorizationCode,
        OAuthProviderConfiguration providerConfiguration,
        OAuthCallbackState? callbackState,
        CancellationToken cancellationToken)
    {
        var redirectUri = string.IsNullOrWhiteSpace(providerConfiguration.RedirectUri)
            ? $"{Request.Scheme}://{Request.Host}{Request.PathBase}{Request.Path}"
            : providerConfiguration.RedirectUri;

        var formValues = new List<KeyValuePair<string, string?>>
        {
            new("grant_type", "authorization_code"),
            new("code", authorizationCode),
            new("client_id", providerConfiguration.ClientId),
            new("redirect_uri", redirectUri)
        };

        if (!string.IsNullOrWhiteSpace(providerConfiguration.ClientSecret))
        {
            formValues.Add(new KeyValuePair<string, string?>("client_secret", providerConfiguration.ClientSecret));
        }

        if (!string.IsNullOrWhiteSpace(callbackState?.Verifier))
        {
            formValues.Add(new KeyValuePair<string, string?>("code_verifier", callbackState.Verifier));
        }

        var request = new HttpRequestMessage(HttpMethod.Post, providerConfiguration.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formValues!)
        };
        request.Headers.Accept.ParseAdd("application/json");

        return await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
    }

    private OAuthCallbackState? TryReadCallbackState()
    {
        var state = Request.Query["state"].ToString();
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        if (!Request.Cookies.TryGetValue(BuildCallbackStateCookieName(state), out var rawCookieValue) || string.IsNullOrWhiteSpace(rawCookieValue))
        {
            return null;
        }

        try
        {
            var callbackState = JsonSerializer.Deserialize<OAuthCallbackState>(Uri.UnescapeDataString(rawCookieValue), JsonSerializerOptions);
            if (callbackState == null)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(callbackState.State)
                ? callbackState with { State = state }
                : callbackState;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Unable to parse OAuth callback state cookie for state {State}.", state);
            return null;
        }
    }

    private void DeleteCallbackStateCookie(string state)
    {
        Response.Cookies.Delete(BuildCallbackStateCookieName(state), new CookieOptions
        {
            Path = "/",
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps
        });
    }

    private async Task<OAuthProviderConfiguration?> ResolveProviderConfigurationAsync(
        string providerKey,
        OAuthCallbackState? callbackState,
        CancellationToken cancellationToken)
    {
        var appInstanceId = callbackState?.AppInstanceId;
        if (appInstanceId.HasValue && appInstanceId.Value != Guid.Empty)
        {
            var appInstance = await _appInstanceRepository.GetByIdAsync(appInstanceId.Value);
            if (appInstance == null)
            {
                return null;
            }

            var appDefinition = await _appDefinitionRepository.GetByIdAsync(appInstance.AppName);
            if (appDefinition == null)
            {
                return null;
            }

            return new OAuthProviderConfiguration
            {
                ClientId = appInstance.ClientId,
                ClientSecret = appInstance.ClientSecret,
                TokenEndpoint = appDefinition.TokenEndpoint,
                SubjectField = appDefinition.SubjectField,
                AppInstanceId = appInstance.Id
            };
        }

        var section = _configuration.GetSection($"Integrations:OAuthProviders:{providerKey}");
        if (!section.Exists())
        {
            return null;
        }

        var configuration = section.Get<OAuthProviderConfiguration>();
        if (configuration == null)
        {
            return null;
        }


        return configuration;
    }

    private string ResolveSubject(
        JsonElement tokenResponse,
        OAuthProviderConfiguration providerConfiguration,
        OAuthCallbackState? callbackState)
    {
        var explicitSubject = Request.Query["subject"].ToString();
        if (!string.IsNullOrWhiteSpace(explicitSubject))
        {
            return Truncate(explicitSubject, 200);
        }

        var subjectCandidates = new[]
        {
            providerConfiguration.SubjectField,
            "sub",
            "subject",
            "user_id",
            "user.id",
            "authed_user.id",
            "owner.id",
            "account.id",
            "team.id"
        };

        foreach (var candidate in subjectCandidates)
        {
            if (TryGetString(tokenResponse, candidate) is { Length: > 0 } value)
            {
                return Truncate(value, 200);
            }
        }

        var idToken = TryGetString(tokenResponse, "id_token");
        if (!string.IsNullOrWhiteSpace(idToken) && TryGetJwtSubject(idToken, out var jwtSubject))
        {
            return Truncate(jwtSubject, 200);
        }

        if (!string.IsNullOrWhiteSpace(callbackState?.IntegrationId) && !string.IsNullOrWhiteSpace(callbackState.State))
        {
            return Truncate($"{callbackState.IntegrationId}:{callbackState.State}", 200);
        }

        var state = Request.Query["state"].ToString();
        if (!string.IsNullOrWhiteSpace(state))
        {
            return Truncate(state, 200);
        }

        return Truncate(Guid.NewGuid().ToString("N"), 200);
    }

    private static DateTimeOffset? ResolveExpiresAtUtc(JsonElement tokenResponse, DateTimeOffset nowUtc)
    {
        if (TryGetLong(tokenResponse, "expires_in") is long expiresIn)
        {
            return nowUtc.AddSeconds(expiresIn);
        }

        if (TryGetLong(tokenResponse, "expires_at") is long expiresAtEpochSeconds)
        {
            return DateTimeOffset.FromUnixTimeSeconds(expiresAtEpochSeconds);
        }

        if (TryGetString(tokenResponse, "expires_at") is { Length: > 0 } expiresAtString
            && DateTimeOffset.TryParse(
                expiresAtString,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedExpiresAt))
        {
            return parsedExpiresAt;
        }

        return null;
    }

    private static string? ExtractErrorDescription(string responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseContent);
            return TryGetString(document.RootElement, "error_description")
                ?? TryGetString(document.RootElement, "error")
                ?? TryGetString(document.RootElement, "message");
        }
        catch (JsonException)
        {
            return responseContent.Length <= 120
                ? responseContent
                : responseContent[..120];
        }
    }

    private static string BuildCallbackStateCookieName(string state)
    {
        var bytes = Encoding.UTF8.GetBytes(state);
        return $"agw_oauth2_{WebEncoders.Base64UrlEncode(bytes)}";
    }

    private string ResolveUiCallbackUrl(OAuthCallbackState? callbackState)
    {
        if (!string.IsNullOrWhiteSpace(callbackState?.UiCallbackUrl))
        {
            return callbackState.UiCallbackUrl;
        }

        var configuredUiCallbackUrl = _configuration["Integrations:UiCallbackUrl"];
        if (!string.IsNullOrWhiteSpace(configuredUiCallbackUrl))
        {
            return configuredUiCallbackUrl;
        }

        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}{IntegrationConstants.UiCallbackPath}";
    }

    private static string? TryGetString(JsonElement element, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!TryGetElement(element, path, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static long? TryGetLong(JsonElement element, string path)
    {
        if (!TryGetElement(element, path, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static bool TryGetElement(JsonElement element, string path, out JsonElement value)
    {
        value = element;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetJwtSubject(string jwt, out string subject)
    {
        subject = string.Empty;
        var segments = jwt.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        try
        {
            var payloadBytes = WebEncoders.Base64UrlDecode(segments[1]);
            using var document = JsonDocument.Parse(payloadBytes);
            var parsedSubject = TryGetString(document.RootElement, "sub");
            if (string.IsNullOrWhiteSpace(parsedSubject))
            {
                return false;
            }

            subject = parsedSubject;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private sealed record OAuthCallbackState
    {
        public string? State { get; init; }
        public Guid AppInstanceId { get; init; }
        public string? IntegrationId { get; init; }
        public string? UiCallbackUrl { get; init; }
        public string? Verifier { get; init; }
        public string? CreatedAt { get; init; }
    }

    private sealed class OAuthProviderConfiguration
    {
        public string ClientId { get; set; } = string.Empty;
        public string? ClientSecret { get; set; }
        public string TokenEndpoint { get; set; } = string.Empty;
        public string? RedirectUri { get; set; }
        public string? SubjectField { get; set; }
        public Guid AppInstanceId { get; set; }
    }

    private sealed record OAuthExchangeResult(string Status, string? Provider = null, string? Subject = null, string? Error = null)
    {
        public static OAuthExchangeResult Succeeded(string provider, string subject) => new("success", provider, subject);

        public static OAuthExchangeResult Failed(string error, string? provider = null) => new("failed", provider, null, error);

        public IReadOnlyList<KeyValuePair<string, string?>> ToQueryParameters()
        {
            var queryParameters = new List<KeyValuePair<string, string?>>
            {
                new("exchange_status", Status)
            };

            if (!string.IsNullOrWhiteSpace(Provider))
            {
                queryParameters.Add(new KeyValuePair<string, string?>("provider", Provider));
            }

            if (!string.IsNullOrWhiteSpace(Subject))
            {
                queryParameters.Add(new KeyValuePair<string, string?>("subject", Subject));
            }

            if (!string.IsNullOrWhiteSpace(Error))
            {
                queryParameters.Add(new KeyValuePair<string, string?>("exchange_error", Error));
            }

            return queryParameters;
        }
    }
}
