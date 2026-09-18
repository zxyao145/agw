using System.Diagnostics;
using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Auth.Security;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Agw.Auth.Application;

public sealed class OidcTicketProcessor
{
    private readonly OidcOptions _settings;
    private readonly IOidcIdentityStore _identities;
    private readonly AuthenticationAttemptLimiter _limiter;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    public OidcTicketProcessor(
        OidcOptions settings,
        IOidcIdentityStore identities,
        AuthenticationAttemptLimiter limiter,
        TimeProvider clock,
        ILoggerFactory loggerFactory
    )
    {
        _settings = settings;
        _identities = identities;
        _limiter = limiter;
        _clock = clock;
        _logger = loggerFactory.CreateLogger("Agw.Auth.Oidc");
    }

    public async Task CompleteAsync(TicketReceivedContext context, string providerId, CookieSecurePolicy securePolicy)
    {
        var started = context.HttpContext.Items[OidcFlow.StartedKey] as long? ?? Stopwatch.GetTimestamp();
        var client = OidcFlow.Value(context.Properties, OidcFlow.ClientKey);
        if (client is not ("web" or "desktop"))
            throw new AgwException(ErrorCodes.AuthenticationRequired);

        var stage = "provisioning";
        try
        {
            var principal = context.Principal ?? throw new AgwException(ErrorCodes.AuthenticationRequired);
            var issuer = OidcFlow.Value(context.Properties, "agw:validated_issuer");
            var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
                throw new AgwException(ErrorCodes.AuthenticationRequired);

            var user = await _identities.ResolveAsync(
                new VerifiedOidcIdentity(
                    providerId,
                    issuer,
                    subject,
                    principal.FindFirst(ClaimTypes.Name)?.Value,
                    principal.FindFirst(ClaimTypes.Email)?.Value
                ),
                context.HttpContext.RequestAborted
            );
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";

            if (client == "desktop")
            {
                var challenge = OidcFlow.Value(context.Properties, OidcFlow.ChallengeKey);
                var state = OidcFlow.Value(context.Properties, OidcFlow.ClientStateKey);
                if (!DesktopLoginProof.IsChallenge(challenge) || !DesktopLoginProof.IsChallenge(state))
                    throw new AgwException(ErrorCodes.DesktopLoginInvalid);

                stage = "desktop-grant";
                var code = await _identities.CreateDesktopGrantAsync(
                    user,
                    providerId,
                    challenge!,
                    context.HttpContext.RequestAborted
                );
                context.HttpContext.Response.Redirect(
                    Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                        OidcFlow.DesktopRedirect,
                        new Dictionary<string, string?> { ["code"] = code, ["state"] = state }
                    )
                );
                context.HandleResponse();
            }
            else
            {
                stage = "session";
                context.Principal = OidcPrincipal.Create(user, providerId);
                var returnPath = OidcFlow.ReturnUrl(context.ReturnUri);
                // Single-origin deployments keep the relative redirect. In a split dev topology the
                // callback runs on the backend origin while the SPA lives on WebBaseUrl; send the
                // browser there. The session cookie is host-scoped (no port isolation), so it follows.
                context.ReturnUri = string.IsNullOrEmpty(_settings.WebBaseUrl)
                    ? returnPath
                    : _settings.WebBaseUrl + returnPath;
                context.HttpContext.Response.Cookies.Append(
                    OidcFlow.ExplicitCookie,
                    "1",
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = securePolicy == CookieSecurePolicy.Always,
                        SameSite = SameSiteMode.Lax,
                        IsEssential = true,
                        Path = "/",
                    }
                );
            }

            OidcTelemetry.Login(providerId, client, "success");
            OidcTelemetry.Callback(providerId, "success", started);
            if (IsOAuth2(providerId))
            {
                OidcTelemetry.OAuth2Login(providerId, client, "success");
                OidcTelemetry.OAuth2Callback(providerId, "success", started);
            }
            _logger.LogInformation(
                "OIDC/OAuth2 login succeeded for {ProviderId}, {Client}, user {UserId}.",
                providerId,
                client,
                user.UserId
            );
        }
        catch (Exception exception) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            OidcTelemetry.Login(providerId, client, "failure");
            OidcTelemetry.Callback(providerId, "failure", started);
            if (IsOAuth2(providerId))
            {
                OidcTelemetry.OAuth2Login(providerId, client, "failure");
                OidcTelemetry.OAuth2Callback(providerId, "failure", started);
            }
            var category = OidcDiagnostics.Failure(context.HttpContext, providerId, client, stage, exception);
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Redirect(OidcFlow.FailureRedirect(_settings, context.Properties, category));
            context.HandleResponse();
        }
    }

    public Task HandleRemoteFailureAsync(RemoteFailureContext context, string providerId, string limiterPrefix)
    {
        var client = OidcFlow.Value(context.Properties, OidcFlow.ClientKey) == "desktop" ? "desktop" : "web";
        var started = context.HttpContext.Items[OidcFlow.StartedKey] as long? ?? Stopwatch.GetTimestamp();
        OidcTelemetry.Login(providerId, client, "failure");
        OidcTelemetry.Callback(providerId, "failure", started);
        if (IsOAuth2(providerId))
        {
            OidcTelemetry.OAuth2Login(providerId, client, "failure");
            OidcTelemetry.OAuth2Callback(providerId, "failure", started);
        }
        var key = limiterPrefix + AuthenticationAttemptLimiter.GetClientKey(context.HttpContext);
        _limiter.RecordFailure(key, _clock.GetUtcNow());
        var stage = context.HttpContext.Items[OidcDiagnostics.StageKey] as string ?? "protocol-validation";
        var category = OidcDiagnostics.Failure(context.HttpContext, providerId, client, stage, context.Failure);
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.HttpContext.Response.Redirect(OidcFlow.FailureRedirect(_settings, context.Properties, category));
        context.HandleResponse();
        return Task.CompletedTask;
    }

    public Task HandleAccessDeniedAsync(AccessDeniedContext context, string providerId)
    {
        var client = OidcFlow.Value(context.Properties, OidcFlow.ClientKey) == "desktop" ? "desktop" : "web";
        var started = context.HttpContext.Items[OidcFlow.StartedKey] as long? ?? Stopwatch.GetTimestamp();
        OidcTelemetry.Login(providerId, client, "cancelled");
        if (IsOAuth2(providerId))
        {
            OidcTelemetry.OAuth2Login(providerId, client, "cancelled");
            OidcTelemetry.OAuth2Callback(providerId, "cancelled", started);
        }
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.HttpContext.Response.Redirect(
            OidcFlow.FailureRedirect(_settings, context.Properties, "authorization-denied")
        );
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private bool IsOAuth2(string providerId) =>
        _settings.Providers.TryGetValue(providerId, out var provider) && provider.Type == AuthProviderType.OAuth2;
}
