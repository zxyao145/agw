using System.Security.Claims;

using Agw.Setup.Middleware;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

using Xunit;

namespace Agw.Setup.Tests;

public class AgwAntiforgeryMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ExecutionHubNegotiate_SkipsValidation()
    {
        var nextCalled = false;
        var middleware = new AgwAntiforgeryMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var antiforgery = new RecordingAntiforgery();
        var context = CreateAuthenticatedPost("/api/hubs/exec/negotiate");

        await middleware.InvokeAsync(context, antiforgery);

        Assert.True(nextCalled);
        Assert.Equal(0, antiforgery.ValidationCount);
    }

    [Fact]
    public async Task InvokeAsync_OtherHubNegotiate_StillValidates()
    {
        var middleware = new AgwAntiforgeryMiddleware(_ => Task.CompletedTask);
        var antiforgery = new RecordingAntiforgery();
        var context = CreateAuthenticatedPost("/api/hubs/other/negotiate");

        await middleware.InvokeAsync(context, antiforgery);

        Assert.Equal(1, antiforgery.ValidationCount);
    }

    private static DefaultHttpContext CreateAuthenticatedPost(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "tester")],
            "AgwCookie"));
        return context;
    }

    private sealed class RecordingAntiforgery : IAntiforgery
    {
        public int ValidationCount { get; private set; }

        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) =>
            throw new NotImplementedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) =>
            throw new NotImplementedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) =>
            throw new NotImplementedException();

        public void SetCookieTokenAndHeader(HttpContext httpContext) =>
            throw new NotImplementedException();

        public Task ValidateRequestAsync(HttpContext httpContext)
        {
            ValidationCount += 1;
            return Task.CompletedTask;
        }
    }
}
