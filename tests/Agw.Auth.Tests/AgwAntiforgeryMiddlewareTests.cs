using System.Security.Claims;
using Agw.Auth.Application;
using Agw.Auth.Middleware;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class AgwAntiforgeryMiddlewareTests
{
    [Theory]
    [InlineData(AgwAuthDefaults.CookieScheme, 1)]
    [InlineData(AgwAuthDefaults.LocalTrustedScheme, 1)]
    [InlineData(AgwAuthDefaults.BearerScheme, 0)]
    public async Task InvokeAsync_UnsafeApiRequest_ValidatesExpectedAuthenticationTypes(
        string authenticationType,
        int expectedValidationCount
    )
    {
        var middleware = new AgwAntiforgeryMiddleware(_ => Task.CompletedTask);
        var antiforgery = new RecordingAntiforgery();
        var context = CreateAuthenticatedRequest(HttpMethods.Post, "/api/projects", authenticationType);

        await middleware.InvokeAsync(context, antiforgery);

        Assert.Equal(expectedValidationCount, antiforgery.ValidationCount);
    }

    [Fact]
    public async Task InvokeAsync_SafeApiRequest_SkipsValidation()
    {
        var middleware = new AgwAntiforgeryMiddleware(_ => Task.CompletedTask);
        var antiforgery = new RecordingAntiforgery();
        var context = CreateAuthenticatedRequest(HttpMethods.Get, "/api/projects", AgwAuthDefaults.CookieScheme);

        await middleware.InvokeAsync(context, antiforgery);

        Assert.Equal(0, antiforgery.ValidationCount);
    }

    [Fact]
    public async Task InvokeAsync_ExecutionHubNegotiate_SkipsValidation()
    {
        var middleware = new AgwAntiforgeryMiddleware(_ => Task.CompletedTask);
        var antiforgery = new RecordingAntiforgery();
        var context = CreateAuthenticatedRequest(
            HttpMethods.Post,
            "/api/hubs/exec/negotiate",
            AgwAuthDefaults.CookieScheme
        );

        await middleware.InvokeAsync(context, antiforgery);

        Assert.Equal(0, antiforgery.ValidationCount);
    }

    [Fact]
    public async Task InvokeAsync_OtherHubNegotiate_StillValidates()
    {
        var middleware = new AgwAntiforgeryMiddleware(_ => Task.CompletedTask);
        var antiforgery = new RecordingAntiforgery();
        var context = CreateAuthenticatedRequest(
            HttpMethods.Post,
            "/api/hubs/other/negotiate",
            AgwAuthDefaults.CookieScheme
        );

        await middleware.InvokeAsync(context, antiforgery);

        Assert.Equal(1, antiforgery.ValidationCount);
    }

    private static DefaultHttpContext CreateAuthenticatedRequest(string method, string path, string authenticationType)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester")], authenticationType)
        );
        return context;
    }

    private sealed class RecordingAntiforgery : IAntiforgery
    {
        public int ValidationCount { get; private set; }

        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotImplementedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotImplementedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => throw new NotImplementedException();

        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotImplementedException();

        public Task ValidateRequestAsync(HttpContext httpContext)
        {
            ValidationCount += 1;
            return Task.CompletedTask;
        }
    }
}
