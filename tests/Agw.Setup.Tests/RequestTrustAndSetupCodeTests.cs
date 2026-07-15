using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;

using Agw.Setup.Contracts;
using Agw.Setup.Middleware;
using Agw.Setup.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Xunit;

namespace Agw.Setup.Tests;

public class RequestTrustAndSetupCodeTests
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

    [Fact]
    public void Consume_WhenCodeIsUsedTwice_OnlyFirstAttemptSucceeds()
    {
        var service = new SetupCodeService("ABCD-EFGH-IJKL");

        Assert.True(service.Matches("ABCD-EFGH-IJKL"));
        Assert.True(service.Consume("ABCD-EFGH-IJKL"));
        Assert.False(service.Matches("ABCD-EFGH-IJKL"));
        Assert.False(service.Consume("ABCD-EFGH-IJKL"));
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
    public async Task AuthenticationMiddleware_WhenLocalTrustedWebSocketHasCrossSiteOrigin_RejectsUpgrade()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpWebSocketFeature>(new WebSocketFeature());
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 5015);
        context.Request.Headers.Origin = "https://evil.example.com";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "LocalTrusted"));
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

    private sealed class WebSocketFeature : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => throw new NotImplementedException();
    }

    private sealed class StateStoreStub : IInitializationStateStore
    {
        public InitializationSnapshot GetSnapshot() => new(true, "hash", 1, []);

        public Task PersistAsync(SetupRequest request, string passwordHash, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public bool ValidateToken(string token) => false;

        public Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
