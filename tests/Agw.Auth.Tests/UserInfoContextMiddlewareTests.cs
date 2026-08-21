using System.Security.Claims;
using Agw.Auth.Application;
using Agw.Auth.Extensions;
using Agw.Auth.Middleware;
using Agw.Shared;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class UserInfoContextMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AuthenticatedRequest_ExposesAndThenRestoresUser()
    {
        var previous = CreatePrincipal("previous", "previous-id");
        var requestUser = CreatePrincipal("admin", "admin-id");
        var userInfoService = new UserInfoService { Current = previous };
        ClaimsPrincipal? observed = null;
        string? observedUserId = null;
        var observedIsAuthenticated = false;
        var middleware = new UserInfoContextMiddleware(_ =>
        {
            observed = userInfoService.Current;
            observedUserId = UserInfoUtil.UserId;
            observedIsAuthenticated = UserInfoUtil.IsAuthenticated;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { User = requestUser };

        try
        {
            await middleware.InvokeAsync(context, userInfoService);

            Assert.Same(requestUser, observed);
            Assert.Equal("admin-id", observedUserId);
            Assert.True(observedIsAuthenticated);
            Assert.Same(previous, userInfoService.Current);
        }
        finally
        {
            UserInfoUtil.Current = null;
        }
    }

    [Fact]
    public async Task InvokeAsync_UnauthenticatedRequest_ExposesNoUser()
    {
        var userInfoService = new UserInfoService();
        ClaimsPrincipal? observed = CreatePrincipal("unexpected", "unexpected-id");
        string? observedUserId = "unexpected-id";
        var observedIsAuthenticated = true;
        var middleware = new UserInfoContextMiddleware(_ =>
        {
            observed = userInfoService.Current;
            observedUserId = UserInfoUtil.UserId;
            observedIsAuthenticated = UserInfoUtil.IsAuthenticated;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();

        try
        {
            await middleware.InvokeAsync(context, userInfoService);

            Assert.Null(observed);
            Assert.Null(observedUserId);
            Assert.False(observedIsAuthenticated);
            Assert.Null(userInfoService.Current);
        }
        finally
        {
            UserInfoUtil.Current = null;
        }
    }

    [Fact]
    public async Task InvokeAsync_ConcurrentRequests_DoNotShareUsers()
    {
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new Dictionary<string, string?>();
        var middleware = new UserInfoContextMiddleware(async context =>
        {
            if (Interlocked.Increment(ref entered) == 2)
                release.SetResult();
            await release.Task;
            lock (observed)
            {
                observed[context.TraceIdentifier] = UserInfoUtil.Current?.Identity?.Name;
            }
        });
        var firstContext = new DefaultHttpContext
        {
            TraceIdentifier = "first",
            User = CreatePrincipal("first-user", "first-id"),
        };
        var secondContext = new DefaultHttpContext
        {
            TraceIdentifier = "second",
            User = CreatePrincipal("second-user", "second-id"),
        };

        try
        {
            await Task.WhenAll(
                middleware.InvokeAsync(firstContext, new UserInfoService()),
                middleware.InvokeAsync(secondContext, new UserInfoService())
            );

            Assert.Equal("first-user", observed["first"]);
            Assert.Equal("second-user", observed["second"]);
            Assert.Null(UserInfoUtil.Current);
        }
        finally
        {
            UserInfoUtil.Current = null;
        }
    }

    [Fact]
    public void UserInfoService_UserId_UsesNameIdentifierAndFallsBackToAdminUserId()
    {
        var service = new UserInfoService { Current = CreatePrincipal("admin", "admin-id") };

        try
        {
            Assert.Equal("admin-id", service.UserId);
            Assert.Equal("admin-id", service.RequiredUserId);
            Assert.True(service.IsAuthenticated);
            Assert.True(UserInfoUtil.IsAuthenticated);

            service.Current = CreatePrincipal("admin", null);

            Assert.Equal(Constants.AdminUserId, service.UserId);
            Assert.Equal(Constants.AdminUserId, UserInfoUtil.UserId);
        }
        finally
        {
            UserInfoUtil.Current = null;
        }
    }

    [Fact]
    public void RequiredUserId_WhenUnauthenticated_ThrowsAuthenticationRequired()
    {
        UserInfoUtil.Current = null;

        var exception = Assert.Throws<AgwException>(() => UserInfoUtil.RequiredUserId);

        Assert.Equal(ErrorCodes.AuthenticationRequired.Code, exception.Code);
        Assert.Equal(ErrorCodes.AuthenticationRequired.StatusCode, exception.StatusCode);
    }

    [Fact]
    public void RequiredUserId_WhenAuthenticatedWithoutUserId_ReturnsFallback()
    {
        UserInfoUtil.Current = new ClaimsPrincipal(new ClaimsIdentity([], AgwAuthDefaults.CookieScheme));

        try
        {
            Assert.True(UserInfoUtil.IsAuthenticated);
            Assert.Equal(Constants.AdminUserId, UserInfoUtil.UserId);
            Assert.Equal("1001", UserInfoUtil.RequiredUserId);
        }
        finally
        {
            UserInfoUtil.Current = null;
        }
    }

    [Fact]
    public void AddAuth_RegistersUserInfoServiceAsScoped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuth();
        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var userInfoService = firstScope.ServiceProvider.GetRequiredService<IUserInfoService>();
        var otherScopeService = secondScope.ServiceProvider.GetRequiredService<IUserInfoService>();

        Assert.NotSame(userInfoService, otherScopeService);
    }

    private static ClaimsPrincipal CreatePrincipal(string name, string? userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        if (userId != null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AgwAuthDefaults.CookieScheme));
    }
}
