using System.Globalization;
using System.Net;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agw.Auth.Application;

public sealed class OAuth2IdentityService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;

    public OAuth2IdentityService(IHttpClientFactory httpClientFactory, IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
    }

    public async Task<VerifiedOidcIdentity> ResolveAsync(
        string providerId,
        OidcProviderOptions provider,
        string accessToken,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new AgwException(ErrorCodes.AuthenticationRequired);

        return provider.IdentitySource == OAuth2IdentitySource.UserInfo
            ? await ResolveUserInfoAsync(providerId, provider, accessToken, cancellationToken)
            : await ResolveAccessTokenAsync(providerId, provider, accessToken, cancellationToken);
    }

    private async Task<VerifiedOidcIdentity> ResolveUserInfoAsync(
        string providerId,
        OidcProviderOptions provider,
        string accessToken,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Agw.Oidc");
            using var request = new HttpRequestMessage(HttpMethod.Get, provider.UserInfoEndpoint);
            request.Headers.Authorization = new("Bearer", accessToken);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw ProviderRejected(response.StatusCode);

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken
            );
            var root = document.RootElement;
            var subject = ReadJsonValue(root, provider.SubjectClaim);
            if (string.IsNullOrWhiteSpace(subject))
                throw new AgwException(ErrorCodes.AuthenticationRequired);

            var identity = new VerifiedOidcIdentity(
                providerId,
                provider.Issuer,
                subject,
                ReadJsonValue(root, provider.DisplayNameClaim),
                ReadJsonValue(root, provider.EmailClaim)
            );
            OidcTelemetry.OAuth2UserInfo(providerId, "success");
            return identity;
        }
        catch (AgwException)
        {
            OidcTelemetry.OAuth2UserInfo(providerId, "failure");
            throw;
        }
        catch (OperationCanceledException)
        {
            OidcTelemetry.OAuth2UserInfo(providerId, "failure");
            throw;
        }
        catch (Exception exception)
        {
            OidcTelemetry.OAuth2UserInfo(providerId, "failure");
            throw ProviderUnavailable(exception);
        }
    }

    private async Task<VerifiedOidcIdentity> ResolveAccessTokenAsync(
        string providerId,
        OidcProviderOptions provider,
        string accessToken,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var signingKeys = await GetSigningKeysAsync(provider.AccessTokenJwksUri, cancellationToken);
            var validation = await ValidateJwtAsync(accessToken, provider, signingKeys, cancellationToken);
            if (validation.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                _cache.Remove(CacheKey(provider.AccessTokenJwksUri));
                signingKeys = await GetSigningKeysAsync(provider.AccessTokenJwksUri, cancellationToken);
                validation = await ValidateJwtAsync(accessToken, provider, signingKeys, cancellationToken);
            }

            if (!validation.IsValid || validation.ClaimsIdentity == null)
                throw InvalidToken("OAuth2 access token validation failed.", validation.Exception);

            var identity = validation.ClaimsIdentity;
            if (
                identity.FindFirst("exp") is not { Value: var expires }
                || !long.TryParse(expires, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            )
                throw InvalidToken("OAuth2 access token expiration is required.");
            var subject = identity.FindFirst(provider.SubjectClaim)?.Value;
            if (string.IsNullOrWhiteSpace(subject))
                throw new AgwException(ErrorCodes.AuthenticationRequired);

            var result = new VerifiedOidcIdentity(
                providerId,
                provider.Issuer,
                subject,
                identity.FindFirst(provider.DisplayNameClaim)?.Value,
                identity.FindFirst(provider.EmailClaim)?.Value
            );
            OidcTelemetry.OAuth2AccessTokenValidation(providerId, "success");
            return result;
        }
        catch (AgwException)
        {
            OidcTelemetry.OAuth2AccessTokenValidation(providerId, "failure");
            throw;
        }
        catch (OperationCanceledException)
        {
            OidcTelemetry.OAuth2AccessTokenValidation(providerId, "failure");
            throw;
        }
        catch (Exception exception)
        {
            OidcTelemetry.OAuth2AccessTokenValidation(providerId, "failure");
            throw InvalidToken("OAuth2 access token validation failed.", exception);
        }
    }

    private async Task<TokenValidationResult> ValidateJwtAsync(
        string token,
        OidcProviderOptions provider,
        IReadOnlyCollection<SecurityKey> signingKeys,
        CancellationToken cancellationToken
    )
    {
        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        return await handler.ValidateTokenAsync(
            token,
            new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = signingKeys,
                RequireSignedTokens = true,
                ValidateIssuer = true,
                ValidIssuer = provider.AccessTokenIssuer,
                ValidateAudience = true,
                ValidAudience = provider.AccessTokenAudience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
            }
        );
    }

    private async Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(
        string jwksUri,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var cached = await _cache.GetOrCreateAsync(
                CacheKey(jwksUri),
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                    var client = _httpClientFactory.CreateClient("Agw.Oidc");
                    using var response = await client.GetAsync(jwksUri, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    return new JsonWebKeySet(content).GetSigningKeys().ToArray();
                }
            );
            return cached
                ?? throw ProviderUnavailable(new InvalidOperationException("OAuth2 signing keys are unavailable."));
        }
        catch (AgwException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ProviderUnavailable(exception);
        }
    }

    private static string CacheKey(string jwksUri) => "agw:oauth2:jwks:" + jwksUri;

    private static AgwException ProviderUnavailable(Exception inner) =>
        new(
            ErrorCodes.AuthenticationRequired,
            "OAuth2 provider is unavailable.",
            new HttpRequestException("OAuth2 provider request failed.", inner)
        );

    // The provider was reached but answered with an error status (e.g. GitHub's 403 when the
    // User-Agent header is missing). Carry the status code so diagnostics can tell this apart
    // from a transport-level failure that never produced a response.
    private static AgwException ProviderRejected(HttpStatusCode status) =>
        new(
            ErrorCodes.AuthenticationRequired,
            "OAuth2 provider rejected the request.",
            new HttpRequestException("OAuth2 UserInfo response was not successful.", null, status)
        );

    private static AgwException InvalidToken(string message, Exception? inner = null) =>
        new(ErrorCodes.AuthenticationRequired, message, inner ?? new SecurityTokenException(message));

    private static string? ReadJsonValue(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.ToString(),
            JsonValueKind.True or JsonValueKind.False => current.ToString(),
            _ => null,
        };
    }
}
