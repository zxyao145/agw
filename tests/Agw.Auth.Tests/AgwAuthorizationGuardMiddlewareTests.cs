using System.Security.Claims;
using Agw.Auth.Middleware;
using Agw.Shared.Runtime;
using Bens.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class AgwAuthorizationGuardMiddlewareTests
{
    [Theory]
    [InlineData("/api/server-info")]
    [InlineData("/api/health/live")]
    [InlineData("/api/health/ready")]
    [InlineData("/api/auth/session")]
    [InlineData("/api/auth/antiforgery")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/integrations/oauth/callback")]
    [InlineData("/api/integrations/oauth/desktop-complete")]
    public async Task InvokeAsync_AnonymousPath_CallsNext(string path)
    {
        var nextCalled = false;
        var middleware = new AgwAuthorizationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context, new InitializationStateStub(true));

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("/api/projects")]
    [InlineData("/a2a/agents")]
    public async Task InvokeAsync_ProtectedPathWithoutAuthentication_ReturnsUnauthorized(string path)
    {
        var nextCalled = false;
        var middleware = new AgwAuthorizationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, new InitializationStateStub(true));

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ProtectedPathWithAuthentication_CallsNext()
    {
        var nextCalled = false;
        var middleware = new AgwAuthorizationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/projects";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "Bearer"));

        await middleware.InvokeAsync(context, new InitializationStateStub(true));

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("/api/projects")]
    [InlineData("/api/hubs/exec")]
    [InlineData("/a2a/agents")]
    public async Task InvokeAsync_UninitializedServer_RejectsProtectedRequest(string path)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiResult();
        using var provider = services.BuildServiceProvider();
        var nextCalled = false;
        var middleware = new AgwAuthorizationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.RequestServices = provider;

        await middleware.InvokeAsync(context, new InitializationStateStub(false));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    private sealed class InitializationStateStub : IServerInitializationState
    {
        public InitializationStateStub(bool isInitialized)
        {
            IsInitialized = isInitialized;
        }

        public bool IsInitialized { get; }
    }
}
