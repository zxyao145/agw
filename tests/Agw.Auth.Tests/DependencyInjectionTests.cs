using System.Security.Claims;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddAuth_ConfiguresCompatibleCookieScheme()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuth();
        services.AddSingleton<IAuthenticationStateStore, StateStoreStub>();
        using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AgwAuthDefaults.CookieScheme);

        Assert.Equal("agw.session", options.Cookie.Name);
        Assert.Equal(TimeSpan.FromHours(12), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("2", true)]
    public async Task CookiePrincipal_SessionVersionMismatch_InvalidatesPrincipal(
        string sessionVersion,
        bool remainsAuthenticated
    )
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuth();
        services.AddSingleton<IAuthenticationStateStore, StateStoreStub>();
        using var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AgwAuthDefaults.CookieScheme);
        var scheme = new AuthenticationScheme(
            AgwAuthDefaults.CookieScheme,
            AgwAuthDefaults.CookieScheme,
            typeof(CookieAuthenticationHandler)
        );
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(AgwAuthDefaults.SessionVersionClaimType, sessionVersion)],
                AgwAuthDefaults.CookieScheme
            )
        );
        var ticket = new AuthenticationTicket(principal, AgwAuthDefaults.CookieScheme);
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var context = new CookieValidatePrincipalContext(httpContext, scheme, options, ticket);

        await options.Events.OnValidatePrincipal(context);

        Assert.Equal(remainsAuthenticated, context.Principal?.Identity?.IsAuthenticated == true);
    }

    private sealed class StateStoreStub : IAuthenticationStateStore
    {
        public AuthenticationSnapshot GetAuthenticationSnapshot() => new("hash", 2);

        public Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
