using System.Security.Claims;

using Agw.Setup.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Host.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    public const string CookieScheme = "AgwCookie";

    private readonly IInitializationStateStore _stateStore;
    private readonly IPasswordHasher<object> _passwordHasher;
    private readonly IAntiforgery _antiforgery;
    private readonly AuthenticationAttemptLimiter _attemptLimiter;
    private readonly TimeProvider _timeProvider;

    public AuthController(
        IInitializationStateStore stateStore,
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
        return AgwApiResult.Ok(new SessionResponse(
            identity?.IsAuthenticated == true,
            identity?.AuthenticationType switch
            {
                "LocalTrusted" => "localTrusted",
                CookieScheme => "cookie",
                "Bearer" => "bearer",
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
        return AgwApiResult.Ok(new AntiforgeryResponse(tokens.RequestToken));
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
            return AgwApiResult.FromError(ErrorCodes.TooManyAuthenticationAttempts);
        }

        var snapshot = _stateStore.GetSnapshot();
        if (snapshot.PasswordHash == null
            || _passwordHasher.VerifyHashedPassword(new object(), snapshot.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            _attemptLimiter.RecordFailure(clientKey, now);
            return AgwApiResult.FromError(ErrorCodes.InvalidAdminCredentials);
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim("session_version", snapshot.SessionVersion.ToString())],
            CookieScheme);
        await HttpContext.SignInAsync(CookieScheme, new ClaimsPrincipal(identity));
        return AgwApiResult.Ok();
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    [ProducesApiResult]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieScheme);
        return AgwApiResult.Ok();
    }

    [HttpPut("password")]
    [ValidateAntiForgeryToken]
    [ProducesApiResult]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!IsInteractiveAdmin()) return AgwApiResult.FromError(ErrorCodes.InteractiveAdminRequired);
        var snapshot = _stateStore.GetSnapshot();
        if (User.Identity?.AuthenticationType == CookieScheme
            && (snapshot.PasswordHash == null
                || _passwordHasher.VerifyHashedPassword(new object(), snapshot.PasswordHash, request.CurrentPassword ?? string.Empty) == PasswordVerificationResult.Failed))
        {
            return AgwApiResult.FromError(ErrorCodes.InvalidAdminCredentials);
        }

        if (request.NewPassword.Length is < 8 or > 256) return AgwApiResult.BadRequest("Password must be between 8 and 256 characters.");
        await _stateStore.UpdatePasswordAsync(_passwordHasher.HashPassword(new object(), request.NewPassword), cancellationToken);
        await HttpContext.SignOutAsync(CookieScheme);
        return AgwApiResult.Ok();
    }

    [HttpGet("tokens")]
    [ProducesApiResult(typeof(ApiTokenSummary[]))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult ListTokens()
    {
        return IsInteractiveAdmin()
            ? AgwApiResult.Ok(_stateStore.GetSnapshot().Tokens)
            : AgwApiResult.FromError(ErrorCodes.InteractiveAdminRequired);
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
        if (!IsInteractiveAdmin()) return AgwApiResult.FromError(ErrorCodes.InteractiveAdminRequired);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 64)
            return AgwApiResult.BadRequest("Token name must be between 1 and 64 characters.");
        return AgwApiResult.Ok(await _stateStore.CreateTokenAsync(request.Name, cancellationToken));
    }

    [HttpDelete("tokens/{id:guid}")]
    [ValidateAntiForgeryToken]
    [ProducesApiResult]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeToken(Guid id, CancellationToken cancellationToken)
    {
        if (!IsInteractiveAdmin()) return AgwApiResult.FromError(ErrorCodes.InteractiveAdminRequired);
        return await _stateStore.RevokeTokenAsync(id, cancellationToken)
            ? AgwApiResult.Ok()
            : AgwApiResult.FromError(ErrorCodes.ApiTokenNotFound);
    }

    private bool IsInteractiveAdmin() => User.Identity?.AuthenticationType is CookieScheme or "LocalTrusted";

    public sealed record LoginRequest(string Password);
    public sealed record ChangePasswordRequest(string? CurrentPassword, string NewPassword);
    public sealed record CreateTokenRequest(string Name);
    public sealed record SessionResponse(bool Authenticated, string AccessMode, int ApiMajorVersion);
    public sealed record AntiforgeryResponse(string? RequestToken);
}
