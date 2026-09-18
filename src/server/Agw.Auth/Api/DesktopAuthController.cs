using System.Diagnostics;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Auth.Security;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Shared.Runtime;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Auth.Api;

[ApiController]
[Route("api/auth/desktop")]
public sealed class DesktopAuthController : ControllerBase
{
    private readonly IOidcIdentityStore _identities;
    private readonly IApiTokenStore _tokens;
    private readonly IServerInitializationState _initialization;
    private readonly AuthenticationAttemptLimiter _limiter;
    private readonly TimeProvider _clock;

    public DesktopAuthController(
        IOidcIdentityStore identities,
        IApiTokenStore tokens,
        IServerInitializationState initialization,
        AuthenticationAttemptLimiter limiter,
        TimeProvider clock
    )
    {
        _identities = identities;
        _tokens = tokens;
        _initialization = initialization;
        _limiter = limiter;
        _clock = clock;
    }

    [HttpPost("exchange")]
    [ProducesApiResult(typeof(DesktopExchangeResponse))]
    public async Task<IActionResult> Exchange(DesktopExchangeRequest request, CancellationToken cancellationToken)
    {
        if (!_initialization.IsInitialized)
            throw new AgwException(ErrorCodes.ServerNotInitialized);
        Response.Headers.CacheControl = "no-store";
        var key = "desktop:" + AuthenticationAttemptLimiter.GetClientKey(HttpContext);
        if (_limiter.IsBlocked(key, _clock.GetUtcNow()))
            return ErrorCodes.TooManyAuthenticationAttempts.ToApiResult();
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await _identities.ExchangeAsync(request.Code, request.CodeVerifier, cancellationToken);
            OidcTelemetry.Exchange("success", started);
            return ApiResult.Ok(result);
        }
        catch (AgwException)
        {
            _limiter.RecordFailure(key, _clock.GetUtcNow());
            OidcTelemetry.Exchange("failure", started);
            throw;
        }
    }

    [HttpPost("logout")]
    [ProducesApiResult]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (!_initialization.IsInitialized)
            throw new AgwException(ErrorCodes.ServerNotInitialized);
        Response.Headers.CacheControl = "no-store";
        // Validate the actual header, never a cookie or LocalTrusted principal.
        var header = Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? await _tokens.ValidateTokenAsync(header["Bearer ".Length..].Trim(), cancellationToken)
            : null;
        if (token?.TokenId is not { } tokenId)
            return ErrorCodes.AuthenticationRequired.ToApiResult();
        using var owner = UserInfoUtil.Push(
            OidcPrincipal.Create(new OidcUser(token.UserId, token.DisplayName ?? "User", 1, token.LoginProvider))
        );
        await _tokens.RevokeTokenAsync(tokenId, cancellationToken);
        return ApiResult.Ok();
    }
}
