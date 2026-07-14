using Agw.Setup.Contracts;
using Agw.Setup.Middleware;
using Agw.Setup.Services;

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

    private sealed class UninitializedStateStore : IInitializationStateStore
    {
        public InitializationSnapshot GetSnapshot() => new(false, string.Empty, 0, []);

        public Task PersistAsync(
            SetupRequest request,
            string passwordHash,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CreatedApiToken> CreateTokenAsync(
            string name,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<bool> RevokeTokenAsync(
            Guid id,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public bool ValidateToken(string token) => false;

        public Task UpdatePasswordAsync(
            string passwordHash,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
