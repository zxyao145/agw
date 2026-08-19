using Agw.Host.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agw.Host.Tests;

public class ClientClosedRequestMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_RequestAbortedCancellation_SetsClientClosedRequestStatus()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellationTokenSource.Token };
        var middleware = new ClientClosedRequestMiddleware(_ => throw new TaskCanceledException("request cancelled"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_NonRequestAbortedCancellation_Rethrows()
    {
        var context = new DefaultHttpContext();
        var middleware = new ClientClosedRequestMiddleware(_ => throw new TaskCanceledException("operation cancelled"));

        var exception = await Assert.ThrowsAsync<TaskCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Equal("operation cancelled", exception.Message);
    }
}
