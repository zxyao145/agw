using System.Diagnostics;
using System.Security.Claims;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Agw.Auth.Extensions;

public static class OidcDependencyInjection
{
    private const string UserAgent = "Agw";

    public static IServiceCollection AddOidcAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        var settings = OidcOptions.Load(configuration, environment);
        services.Replace(ServiceDescriptor.Singleton(settings));
        services.AddHostedService<DesktopGrantCleanupService>();
        services.AddMemoryCache();
        services.AddSingleton<OAuth2IdentityService>();
        services.AddScoped<OidcTicketProcessor>();
        services.AddHttpClient(
            "Agw.Oidc",
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                // Some providers (notably GitHub's REST API at api.github.com) reject requests that
                // carry no User-Agent with HTTP 403. .NET's HttpClient sends none by default, so set
                // an explicit product token for every backchannel/userinfo/JWKS call.
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            }
        );
        var authentication = services.AddAuthentication();
        foreach (var (id, provider) in settings.Providers.Where(pair => pair.Value.Enabled))
        {
            var scheme = (provider.Type == AuthProviderType.OAuth2 ? "oauth2:" : "oidc:") + id;
            var callback = "/api/auth/oidc/callback/" + id;
            var secure = settings.PublicBaseUrl.StartsWith("https://", StringComparison.Ordinal)
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            if (provider.Type == AuthProviderType.OAuth2)
            {
                authentication.AddOAuth<AgwOAuthOptions, AgwOAuthHandler>(
                    scheme,
                    options =>
                    {
                        options.ProviderId = id;
                        options.Provider = provider;
                        options.PublicCallbackUri = settings.PublicBaseUrl + callback;
                        options.ClientId = provider.ClientId;
                        options.ClientSecret = provider.ClientSecret;
                        options.AuthorizationEndpoint = provider.AuthorizationEndpoint;
                        options.TokenEndpoint = provider.TokenEndpoint;
                        options.UserInformationEndpoint = provider.UserInfoEndpoint;
                        options.SignInScheme = AgwAuthDefaults.CookieScheme;
                        options.CallbackPath = callback;
                        options.UsePkce = provider.UsePkce;
                        options.SaveTokens = false;
                        options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
                        options.Scope.Clear();
                        foreach (var scope in provider.Scopes)
                            options.Scope.Add(scope);
                        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                        options.CorrelationCookie.SecurePolicy = secure;
                        options.Events = new OAuthEvents
                        {
                            OnCreatingTicket = async context =>
                            {
                                context.HttpContext.Items[OidcDiagnostics.StageKey] =
                                    provider.IdentitySource == OAuth2IdentitySource.UserInfo
                                        ? "oauth-userinfo"
                                        : "oauth-access-token-validation";
                                var identity = await context
                                    .HttpContext.RequestServices.GetRequiredService<OAuth2IdentityService>()
                                    .ResolveAsync(
                                        id,
                                        provider,
                                        context.AccessToken ?? string.Empty,
                                        context.HttpContext.RequestAborted
                                    );
                                var claimsIdentity =
                                    context.Identity ?? throw new AgwException(ErrorCodes.AuthenticationRequired);
                                claimsIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, identity.Subject));
                                claimsIdentity.AddClaim(
                                    new Claim(ClaimTypes.Name, identity.DisplayName ?? identity.Subject)
                                );
                                if (!string.IsNullOrWhiteSpace(identity.Email))
                                    claimsIdentity.AddClaim(new Claim(ClaimTypes.Email, identity.Email));
                                context.Properties.Items["agw:validated_issuer"] = identity.Issuer;
                            },
                            OnTicketReceived = context =>
                                context
                                    .HttpContext.RequestServices.GetRequiredService<OidcTicketProcessor>()
                                    .CompleteAsync(context, id, secure),
                            OnRemoteFailure = context =>
                                context
                                    .HttpContext.RequestServices.GetRequiredService<OidcTicketProcessor>()
                                    .HandleRemoteFailureAsync(context, id, "oauth2:"),
                            OnAccessDenied = context =>
                                context
                                    .HttpContext.RequestServices.GetRequiredService<OidcTicketProcessor>()
                                    .HandleAccessDeniedAsync(context, id),
                        };
                    }
                );
                services
                    .AddOptions<AgwOAuthOptions>(scheme)
                    .Configure<IHttpClientFactory>(
                        (options, factory) => options.Backchannel = factory.CreateClient("Agw.Oidc")
                    );
                continue;
            }
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
                            if (context.Principal?.Identity is ClaimsIdentity identity)
                            {
                                AddClaimIfMissing(
                                    identity,
                                    ClaimTypes.NameIdentifier,
                                    identity.FindFirst("sub")?.Value
                                );
                                AddClaimIfMissing(identity, ClaimTypes.Name, identity.FindFirst("name")?.Value);
                                AddClaimIfMissing(identity, ClaimTypes.Email, identity.FindFirst("email")?.Value);
                            }
                            return Task.CompletedTask;
                        },
                        OnTicketReceived = context =>
                            context
                                .HttpContext.RequestServices.GetRequiredService<OidcTicketProcessor>()
                                .CompleteAsync(context, id, secure),
                        OnRemoteFailure = context =>
                            context
                                .HttpContext.RequestServices.GetRequiredService<OidcTicketProcessor>()
                                .HandleRemoteFailureAsync(context, id, "oidc:"),
                        OnAccessDenied = context =>
                            context
                                .HttpContext.RequestServices.GetRequiredService<OidcTicketProcessor>()
                                .HandleAccessDeniedAsync(context, id),
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

    private static void AddClaimIfMissing(ClaimsIdentity identity, string type, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !identity.HasClaim(claim => claim.Type == type))
            identity.AddClaim(new Claim(type, value));
    }
}
