using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Auth.Security;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Shared.Runtime;
using Bens.Results;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Auth.Api;

[ApiController]
[Route("api/auth/oidc")]
public sealed class OidcController : ControllerBase
{
    private readonly OidcOptions _options;
    private readonly IServerInitializationState _initialization;
    private readonly AuthenticationAttemptLimiter _limiter;
    private readonly TimeProvider _clock;

    public OidcController(
        OidcOptions options,
        IServerInitializationState initialization,
        AuthenticationAttemptLimiter limiter,
        TimeProvider clock
    )
    {
        _options = options;
        _initialization = initialization;
        _limiter = limiter;
        _clock = clock;
    }

    [HttpGet("providers")]
    [ProducesApiResult(typeof(OidcProviderResponse[]))]
    public IActionResult Providers()
    {
        EnsureInitialized();
        Response.Headers.CacheControl = "no-store";
        return ApiResult.Ok(_options.ListProviders());
    }

    [HttpGet("login")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(
        [FromQuery] string providerId,
        [FromQuery] string client = "web",
        [FromQuery] string? returnUrl = null,
        [FromQuery] string? clientState = null,
        [FromQuery] string? codeChallenge = null,
        [FromQuery] string? codeChallengeMethod = null
    )
    {
        EnsureInitialized();
        var provider = _options.RequireProvider(providerId);
        var properties = OidcFlow.Properties(client, returnUrl, clientState, codeChallenge, codeChallengeMethod);
        var limiterPrefix = provider.Type == AuthProviderType.OAuth2 ? "oauth2:" : "oidc:";
        if (
            _limiter.IsBlocked(
                limiterPrefix + AuthenticationAttemptLimiter.GetClientKey(HttpContext),
                _clock.GetUtcNow()
            )
        )
            return ErrorCodes.TooManyAuthenticationAttempts.ToApiResult();
        Response.Headers.CacheControl = "no-store";
        // Correlation cookies must be set on the public callback origin.
        var publicOrigin = new Uri(_options.PublicBaseUrl);
        // Cookies are host-scoped; a development proxy may use another backend port.
        if (
            !string.Equals(Request.Scheme, publicOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Request.Host.Host, publicOrigin.Host, StringComparison.OrdinalIgnoreCase)
        )
            return Redirect(_options.PublicBaseUrl + Request.Path + Request.QueryString);
        OidcTelemetry.Login(providerId, client, "started");
        if (provider.Type == AuthProviderType.OAuth2)
        {
            OidcTelemetry.OAuth2Login(providerId, client, "started");
            OidcTelemetry.OAuth2Pkce(providerId, provider.UsePkce);
        }
        try
        {
            var scheme = (provider.Type == AuthProviderType.OAuth2 ? "oauth2:" : "oidc:") + providerId;
            await HttpContext.ChallengeAsync(scheme, properties);
            return new EmptyResult();
        }
        catch (Exception exception) when (!HttpContext.RequestAborted.IsCancellationRequested && !Response.HasStarted)
        {
            var category = OidcDiagnostics.Failure(HttpContext, providerId, client, "challenge", exception);
            OidcTelemetry.Login(providerId, client, "failure");
            if (provider.Type == AuthProviderType.OAuth2)
                OidcTelemetry.OAuth2Login(providerId, client, "failure");
            return Redirect(OidcFlow.FailureRedirect(_options, properties, category));
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialization.IsInitialized)
            throw new AgwException(ErrorCodes.ServerNotInitialized);
    }
}
