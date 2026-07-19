using System.Security.Claims;

using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Shared;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;

using Bens.Results;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Auth.Api;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthenticationStateStore _stateStore;
    private readonly IPasswordHasher<object> _passwordHasher;
    private readonly IAntiforgery _antiforgery;
    private readonly AuthenticationAttemptLimiter _attemptLimiter;
    private readonly TimeProvider _timeProvider;

    public AuthController(
        IAuthenticationStateStore stateStore,
        IPasswordHasher<object> passwordHasher,
        IAntiforgery antiforgery,
        AuthenticationAttemptLimiter attemptLimiter,
        TimeProvider timeProvider)
    {
        _stateStore = stateStore;
        _passwordHasher = passwordHasher;
        _antiforgery = antiforgery;
        _attemptLimiter = attemptLimiter;
        _timeProvider = timeProvider;
    }

    [HttpGet("session")]
    [ProducesApiResult(typeof(SessionResponse))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Session()
    {
        var identity = User.Identity;
        return ApiResult.Ok(new SessionResponse(
            identity?.IsAuthenticated == true,
            identity?.AuthenticationType switch
            {
                AgwAuthDefaults.LocalTrustedScheme => "localTrusted",
                AgwAuthDefaults.CookieScheme => "cookie",
                AgwAuthDefaults.BearerScheme => "bearer",
                _ => "anonymous"
            },
            1));
    }

    [HttpGet("antiforgery")]
    [ProducesApiResult(typeof(AntiforgeryResponse))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Antiforgery()
    {
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        return ApiResult.Ok(new AntiforgeryResponse(tokens.RequestToken));
    }

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    [ProducesApiResult]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var clientKey = AuthenticationAttemptLimiter.GetClientKey(HttpContext);
        var now = _timeProvider.GetUtcNow();
        if (_attemptLimiter.IsBlocked(clientKey, now))
        {
            return ErrorCodes.TooManyAuthenticationAttempts.ToApiResult();
        }

        var snapshot = _stateStore.GetAuthenticationSnapshot();
        if (snapshot.PasswordHash == null
            || _passwordHasher.VerifyHashedPassword(new object(), snapshot.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            _attemptLimiter.RecordFailure(clientKey, now);
            return ErrorCodes.InvalidAdminCredentials.ToApiResult();
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, Constants.AdminUserName),
                new Claim(AgwAuthDefaults.SessionVersionClaimType, snapshot.SessionVersion.ToString())
            ],
            AgwAuthDefaults.CookieScheme);
        await HttpContext.SignInAsync(AgwAuthDefaults.CookieScheme, new ClaimsPrincipal(identity));
        return ApiResult.Ok();
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    [ProducesApiResult]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AgwAuthDefaults.CookieScheme);
        return ApiResult.Ok();
    }

    [HttpPut("password")]
    [ValidateAntiForgeryToken]
    [ProducesApiResult]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!IsInteractiveAdmin()) return ErrorCodes.InteractiveAdminRequired.ToApiResult();
        var snapshot = _stateStore.GetAuthenticationSnapshot();
        if (User.Identity?.AuthenticationType == AgwAuthDefaults.CookieScheme
            && (snapshot.PasswordHash == null
                || _passwordHasher.VerifyHashedPassword(
                    new object(),
                    snapshot.PasswordHash,
                    request.CurrentPassword ?? string.Empty) == PasswordVerificationResult.Failed))
        {
            return ErrorCodes.InvalidAdminCredentials.ToApiResult();
        }

        if (request.NewPassword.Length is < 8 or > 256)
        {
            return ApiResult.BadRequest(
                "Password must be between 8 and 256 characters.",
                ErrorCodes.InvalidParam.Code);
        }
        await _stateStore.UpdatePasswordAsync(
            _passwordHasher.HashPassword(new object(), request.NewPassword),
            cancellationToken);
        await HttpContext.SignOutAsync(AgwAuthDefaults.CookieScheme);
        return ApiResult.Ok();
    }

    [HttpGet("tokens")]
    [ProducesApiResult(typeof(ApiTokenSummary[]))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult ListTokens()
    {
        return IsInteractiveAdmin()
            ? ApiResult.Ok(_stateStore.GetAuthenticationSnapshot().Tokens)
            : ErrorCodes.InteractiveAdminRequired.ToApiResult();
    }

    [HttpPost("tokens")]
    [ValidateAntiForgeryToken]
    [ProducesApiResult(typeof(CreatedApiToken))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateToken(CreateTokenRequest request, CancellationToken cancellationToken)
    {
        if (!IsInteractiveAdmin()) return ErrorCodes.InteractiveAdminRequired.ToApiResult();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 64)
            return ApiResult.BadRequest(
                "Token name must be between 1 and 64 characters.",
                ErrorCodes.InvalidParam.Code);
        return ApiResult.Ok(await _stateStore.CreateTokenAsync(request.Name, cancellationToken));
    }

    [HttpDelete("tokens/{id:guid}")]
    [ValidateAntiForgeryToken]
    [ProducesApiResult]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeToken(Guid id, CancellationToken cancellationToken)
    {
        if (!IsInteractiveAdmin()) return ErrorCodes.InteractiveAdminRequired.ToApiResult();
        return await _stateStore.RevokeTokenAsync(id, cancellationToken)
            ? ApiResult.Ok()
            : ErrorCodes.ApiTokenNotFound.ToApiResult();
    }

    private bool IsInteractiveAdmin() =>
        User.Identity?.AuthenticationType is AgwAuthDefaults.CookieScheme or AgwAuthDefaults.LocalTrustedScheme;
}
