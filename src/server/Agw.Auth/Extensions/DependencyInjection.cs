using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Auth.Domain.Repositories;
using Agw.Auth.Domain.Services;
using Agw.Auth.Infrastructure;
using Agw.Auth.Security;
using Agw.Shared.Contracts;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agw.Auth.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddAuth(this IServiceCollection services)
    {
        services.TryAddSingleton<OidcOptions>();
        services.TryAddSingleton<IOidcProviderAvailability>(provider => provider.GetRequiredService<OidcOptions>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "agw.antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        services
            .AddAuthentication(AgwAuthDefaults.CookieScheme)
            .AddCookie(
                AgwAuthDefaults.CookieScheme,
                options =>
                {
                    options.Cookie.Name = "agw.session";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.ExpireTimeSpan = TimeSpan.FromHours(12);
                    options.SlidingExpiration = true;
                    options.Events = new CookieAuthenticationEvents
                    {
                        OnRedirectToLogin = context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        },
                        OnRedirectToAccessDenied = context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return Task.CompletedTask;
                        },
                        OnValidatePrincipal = OidcCookieValidator.ValidateAsync,
                    };
                }
            );
        services.AddAuthorization();
        services.TryAddSingleton<IPasswordHasher<object>, PasswordHasher<object>>();
        services.TryAddSingleton<AuthenticationAttemptLimiter>();
        services.TryAddScoped<UserInfoService>();
        services.TryAddScoped<IUserInfoService>(provider => provider.GetRequiredService<UserInfoService>());
        services.TryAddScoped<ICurrentUser>(provider => provider.GetRequiredService<UserInfoService>());
        services.TryAddScoped<IApiTokenRepository, ApiTokenRepository>();
        services.TryAddScoped<ApiTokenNameUniquenessDomainService>();
        services.AddHybridCache();
        services.TryAddScoped<IApiTokenStore, ApiTokenAppService>();
        return services;
    }
}
