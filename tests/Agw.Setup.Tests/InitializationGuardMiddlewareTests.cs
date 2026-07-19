using Agw.Setup.Middleware;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;

using Microsoft.AspNetCore.Http;

using Xunit;

namespace Agw.Setup.Tests;

public class InitializationGuardMiddlewareTests
{
    [Theory]
    [InlineData("/api/health/live")]
    [InlineData("/api/health/ready")]
    public async Task InvokeAsync_WhenUninitializedAndApiHealthPath_CallsNext(string path)
    {
        var nextCalled = false;
        var middleware = new InitializationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context, new UninitializedStateStore());

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task InvokeAsync_WhenUninitializedAndLegacyHealthPath_RedirectsToSetup(string path)
    {
        var nextCalled = false;
        var middleware = new InitializationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;

        await middleware.InvokeAsync(context, new UninitializedStateStore());

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/setup", context.Response.Headers.Location);
    }

    private sealed class UninitializedStateStore : IServerInitializationState
    {
        public bool IsInitialized => false;
        public DatabaseProvider DatabaseProvider => DatabaseProvider.Sqlite;
        public string DatabaseConnectionString => string.Empty;
    }
}
