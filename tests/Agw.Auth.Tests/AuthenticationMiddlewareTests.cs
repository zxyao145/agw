using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;

using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Auth.Middleware;
using Agw.Auth.Security;
using Agw.Shared;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Xunit;

namespace Agw.Auth.Tests;

public sealed class AuthenticationMiddlewareTests
{
    [Fact]
    public void IsLocalTrusted_WhenLoopbackHasNoForwardingHeaders_ReturnsTrue()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("localhost", 5015);

        Assert.True(LocalTrustedRequest.IsLocalTrusted(context));
    }

    [Fact]
    public void IsLocalTrusted_WhenLoopbackHasForwardingHeaders_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("agw.example.com");
        context.Request.Headers["X-Forwarded-Host"] = "agw.example.com";

        Assert.False(LocalTrustedRequest.IsLocalTrusted(context));
    }

    [Theory]
    [InlineData("https://agw.example.com", true)]
    [InlineData("https://evil.example.com", false)]
    public void IsSameOrigin_WhenOriginVaries_ReturnsExpected(string origin, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("agw.example.com");
        context.Request.Headers.Origin = origin;

        Assert.Equal(expected, LocalTrustedRequest.IsSameOrigin(context));
    }

    [Theory]
    [InlineData("agw://app", true)]
    [InlineData("agw://app/", true)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("agw://evil", false)]
    public void IsDesktopOrigin_WhenOriginVaries_ReturnsExpected(string origin, bool expected)
    {
        Assert.Equal(expected, LocalTrustedRequest.IsDesktopOrigin(origin));
    }

    [Fact]
    public void AuthenticationAttemptLimiter_BlocksAfterFiveFailuresWithinFifteenMinutes()
    {
        var limiter = new AuthenticationAttemptLimiter();
        var now = DateTimeOffset.Parse("2026-07-10T00:00:00Z");

        for (var i = 0; i < 5; i++) limiter.RecordFailure("192.0.2.1", now.AddMinutes(i));

        Assert.True(limiter.IsBlocked("192.0.2.1", now.AddMinutes(14)));
        Assert.False(limiter.IsBlocked("192.0.2.2", now.AddMinutes(14)));
        Assert.False(limiter.IsBlocked("192.0.2.1", now.AddMinutes(20)));
    }

    [Fact]
    public async Task InvokeAsync_ValidBearerToken_CreatesBearerPrincipal()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer agw_desktop";
        var middleware = new AgwAuthenticationMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, new StateStoreStub("agw_desktop"));

        Assert.True(context.User.Identity?.IsAuthenticated);
        Assert.Equal(AgwAuthDefaults.BearerScheme, context.User.Identity?.AuthenticationType);
        Assert.Equal(Constants.AdminUserName, context.User.Identity?.Name);
    }

    [Fact]
    public async Task InvokeAsync_InvalidBearerToken_DoesNotCreatePrincipalForRemoteRequest()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        context.Request.Host = new HostString("agw.example.com");
        context.Request.Headers.Authorization = "Bearer agw_invalid";
        var middleware = new AgwAuthenticationMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, new StateStoreStub("agw_valid"));

        Assert.False(context.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task InvokeAsync_InvalidBearerTokenOnLoopback_FallsBackToLocalTrustedPrincipal()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("localhost", 5015);
        context.Request.Headers.Authorization = "Bearer agw_invalid";
        var middleware = new AgwAuthenticationMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, new StateStoreStub("agw_valid"));

        Assert.Equal(AgwAuthDefaults.LocalTrustedScheme, context.User.Identity?.AuthenticationType);
    }

    [Fact]
    public async Task InvokeAsync_LocalTrustedRequest_CreatesLocalTrustedPrincipal()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("localhost", 5015);
        var middleware = new AgwAuthenticationMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, new StateStoreStub());

        Assert.Equal(AgwAuthDefaults.LocalTrustedScheme, context.User.Identity?.AuthenticationType);
    }

    [Fact]
    public async Task InvokeAsync_LocalTrustedWebSocketWithCrossSiteOrigin_RejectsUpgrade()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpWebSocketFeature>(new WebSocketFeature());
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 5015);
        context.Request.Headers.Origin = "https://evil.example.com";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, Constants.AdminUserName)],
            AgwAuthDefaults.LocalTrustedScheme));
        var nextCalled = false;
        var middleware = new AgwAuthenticationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, new StateStoreStub());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_DesktopWebSocketWithBearerToken_AllowsUpgrade()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpWebSocketFeature>(new WebSocketFeature());
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 30815);
        context.Request.Headers.Origin = "agw://app";
        context.Request.Headers.Authorization = "Bearer agw_desktop";
        var nextCalled = false;
        var middleware = new AgwAuthenticationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, new StateStoreStub("agw_desktop"));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    private sealed class WebSocketFeature : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => throw new NotImplementedException();
    }

    private sealed class StateStoreStub : IAuthenticationStateStore
    {
        private readonly string? _validToken;

        public StateStoreStub(string? validToken = null)
        {
            _validToken = validToken;
        }

        public AuthenticationSnapshot GetAuthenticationSnapshot() => new("hash", 1, []);

        public Task<CreatedApiToken> CreateTokenAsync(
            string name,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<bool> RevokeTokenAsync(
            Guid id,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public bool ValidateToken(string token) => string.Equals(token, _validToken, StringComparison.Ordinal);

        public Task UpdatePasswordAsync(
            string passwordHash,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
