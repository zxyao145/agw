using System.Diagnostics;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Auth.Security;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Agw.Auth.Extensions;

public static class OidcDependencyInjection
{
    public static IServiceCollection AddOidcAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        var settings = OidcOptions.Load(configuration, environment);
        services.Replace(ServiceDescriptor.Singleton(settings));
        services.AddHostedService<DesktopGrantCleanupService>();
        services.AddHttpClient("Agw.Oidc", client => client.Timeout = TimeSpan.FromSeconds(15));
        var authentication = services.AddAuthentication();
        foreach (var (id, provider) in settings.Providers.Where(pair => pair.Value.Enabled))
        {
            var scheme = "oidc:" + id;
            var callback = "/api/auth/oidc/callback/" + id;
            authentication.AddOpenIdConnect(
                scheme,
                options =>
                {
                    options.Authority = provider.Authority;
                    options.ClientId = provider.ClientId;
                    options.ClientSecret = provider.ClientSecret;
                    options.SignInScheme = AgwAuthDefaults.CookieScheme;
                    options.CallbackPath = callback;
                    options.ResponseType = OpenIdConnectResponseType.Code;
                    options.ResponseMode = OpenIdConnectResponseMode.Query;
                    options.UsePkce = true;
                    options.MapInboundClaims = false;
                    options.SaveTokens = false;
                    options.UseTokenLifetime = false;
                    options.GetClaimsFromUserInfoEndpoint = false;
                    options.RequireHttpsMetadata =
                        !environment.IsDevelopment() || !new Uri(provider.Authority).IsLoopback;
                    options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
                    options.Scope.Clear();
                    options.Scope.Add("openid");
                    options.Scope.Add("profile");
                    options.Scope.Add("email");
                    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                    options.NonceCookie.SameSite = SameSiteMode.Lax;
                    var secure = settings.PublicBaseUrl.StartsWith("https://", StringComparison.Ordinal)
                        ? CookieSecurePolicy.Always
                        : CookieSecurePolicy.SameAsRequest;
                    options.CorrelationCookie.SecurePolicy = secure;
                    options.NonceCookie.SecurePolicy = secure;
                    options.Events = new OpenIdConnectEvents
                    {
                        OnRedirectToIdentityProvider = context =>
                        {
                            context.ProtocolMessage.RedirectUri = settings.PublicBaseUrl + callback;
                            return Task.CompletedTask;
                        },
                        OnAuthorizationCodeReceived = context =>
                        {
                            context.HttpContext.Items[OidcDiagnostics.StageKey] = "token-exchange";
                            // This verifier belongs to the Server's OIDC handler, never the Desktop.
                            context.TokenEndpointRequest!.RedirectUri = settings.PublicBaseUrl + callback;
                            return Task.CompletedTask;
                        },
                        OnMessageReceived = context =>
                        {
                            context.HttpContext.Items[OidcFlow.StartedKey] = Stopwatch.GetTimestamp();
                            context.HttpContext.Items[OidcDiagnostics.StageKey] = "protocol-validation";
                            if (
                                !HttpMethods.IsGet(context.Request.Method)
                                || !context
                                    .HttpContext.RequestServices.GetRequiredService<IServerInitializationState>()
                                    .IsInitialized
                            )
                                context.Fail("OIDC callback is not available.");
                            return Task.CompletedTask;
                        },
                        OnTokenValidated = context =>
                        {
                            context.HttpContext.Items[OidcDiagnostics.StageKey] = "token-validation";
                            // Default claim actions remove "iss" before OnTicketReceived. Capture only
                            // the validated token metadata here; provisioning still waits for all checks.
                            context.Properties!.Items["agw:validated_issuer"] = context.SecurityToken.Issuer;
                            return Task.CompletedTask;
                        },
                        OnTicketReceived = async context =>
                        {
                            var started =
                                context.HttpContext.Items[OidcFlow.StartedKey] as long? ?? Stopwatch.GetTimestamp();
                            var client = OidcFlow.Value(context.Properties, OidcFlow.ClientKey);
                            if (client is not ("web" or "desktop"))
                                throw new AgwException(ErrorCodes.AuthenticationRequired);
                            var stage = "provisioning";
                            try
                            {
                                // All protocol checks, including nonce, have completed before provisioning.
                                var external = context.Principal!;
                                var store =
                                    context.HttpContext.RequestServices.GetRequiredService<IOidcIdentityStore>();
                                var user = await store.ResolveAsync(
                                    new VerifiedOidcIdentity(
                                        id,
                                        OidcFlow.Value(context.Properties, "agw:validated_issuer") ?? string.Empty,
                                        external.FindFirst("sub")?.Value ?? string.Empty,
                                        external.FindFirst("name")?.Value,
                                        external.FindFirst("email")?.Value
                                    ),
                                    context.HttpContext.RequestAborted
                                );
                                context.Response.Headers.CacheControl = "no-store";
                                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                                if (client == "desktop")
                                {
                                    var challenge = OidcFlow.Value(context.Properties, OidcFlow.ChallengeKey);
                                    var state = OidcFlow.Value(context.Properties, OidcFlow.ClientStateKey);
                                    if (
                                        !DesktopLoginProof.IsChallenge(challenge)
                                        || !DesktopLoginProof.IsChallenge(state)
                                    )
                                        throw new AgwException(ErrorCodes.DesktopLoginInvalid);
                                    stage = "desktop-grant";
                                    var code = await store.CreateDesktopGrantAsync(
                                        user,
                                        id,
                                        challenge!,
                                        context.HttpContext.RequestAborted
                                    );
                                    context.Response.Redirect(
                                        QueryHelpers.AddQueryString(
                                            OidcFlow.DesktopRedirect,
                                            new Dictionary<string, string?> { ["code"] = code, ["state"] = state }
                                        )
                                    );
                                    context.HandleResponse();
                                }
                                else
                                {
                                    stage = "session";
                                    context.Principal = OidcPrincipal.Create(user, id);
                                    context.ReturnUri = OidcFlow.ReturnUrl(context.ReturnUri);
                                    context.Response.Cookies.Append(
                                        OidcFlow.ExplicitCookie,
                                        "1",
                                        new CookieOptions
                                        {
                                            HttpOnly = true,
                                            Secure = secure == CookieSecurePolicy.Always,
                                            SameSite = SameSiteMode.Lax,
                                            IsEssential = true,
                                            Path = "/",
                                        }
                                    );
                                }
                                OidcTelemetry.Login(id, client, "success");
                                OidcTelemetry.Callback(id, "success", started);
                                context
                                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                                    .CreateLogger("Agw.Auth.Oidc")
                                    .LogInformation(
                                        "OIDC login succeeded for {ProviderId}, {Client}, user {UserId}.",
                                        id,
                                        client,
                                        user.UserId
                                    );
                            }
                            catch (Exception exception)
                                when (!context.HttpContext.RequestAborted.IsCancellationRequested)
                            {
                                OidcTelemetry.Login(id, client, "failure");
                                OidcTelemetry.Callback(id, "failure", started);
                                var category = OidcDiagnostics.Failure(
                                    context.HttpContext,
                                    id,
                                    client,
                                    stage,
                                    exception
                                );
                                context.Response.Headers.CacheControl = "no-store";
                                context.Response.Redirect(
                                    OidcFlow.FailureRedirect(settings, context.Properties, category)
                                );
                                context.HandleResponse();
                            }
                        },
                        OnRemoteFailure = context =>
                        {
                            var client =
                                OidcFlow.Value(context.Properties, OidcFlow.ClientKey) == "desktop" ? "desktop" : "web";
                            var started =
                                context.HttpContext.Items[OidcFlow.StartedKey] as long? ?? Stopwatch.GetTimestamp();
                            OidcTelemetry.Login(id, client, "failure");
                            OidcTelemetry.Callback(id, "failure", started);
                            var limiter =
                                context.HttpContext.RequestServices.GetRequiredService<AuthenticationAttemptLimiter>();
                            var clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
                            limiter.RecordFailure(
                                "oidc:" + AuthenticationAttemptLimiter.GetClientKey(context.HttpContext),
                                clock.GetUtcNow()
                            );
                            var stage =
                                context.HttpContext.Items[OidcDiagnostics.StageKey] as string ?? "protocol-validation";
                            var category = OidcDiagnostics.Failure(
                                context.HttpContext,
                                id,
                                client,
                                stage,
                                context.Failure
                            );
                            context.Response.Headers.CacheControl = "no-store";
                            context.Response.Redirect(OidcFlow.FailureRedirect(settings, context.Properties, category));
                            context.HandleResponse();
                            return Task.CompletedTask;
                        },
                        OnAccessDenied = context =>
                        {
                            OidcTelemetry.Login(
                                id,
                                OidcFlow.Value(context.Properties, OidcFlow.ClientKey) == "desktop" ? "desktop" : "web",
                                "cancelled"
                            );
                            context.Response.Headers.CacheControl = "no-store";
                            context.Response.Redirect(
                                OidcFlow.FailureRedirect(settings, context.Properties, "authorization-denied")
                            );
                            context.HandleResponse();
                            return Task.CompletedTask;
                        },
                    };
                }
            );
            services
                .AddOptions<OpenIdConnectOptions>(scheme)
                .Configure<IHttpClientFactory>(
                    (options, factory) => options.Backchannel = factory.CreateClient("Agw.Oidc")
                );
        }
        return services;
    }
}
