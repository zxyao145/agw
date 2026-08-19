using System.Diagnostics;
using System.Net;
using Agw.Host.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Agw.Host.Tests;

public class TraceIdResponseHeaderMiddlewareTests
{
    [Fact]
    public async Task Response_IncludesCurrentActivityTraceId()
    {
        string? traceId = null;
        await using var app = await CreateAppAsync(webApp =>
        {
            webApp.MapGet(
                "/probe",
                () =>
                {
                    traceId = Activity.Current?.TraceId.ToString();
                    return Results.NoContent();
                }
            );
        });

        var response = await app.GetTestClient().GetAsync("/probe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(traceId);
        var responseTraceId = Assert.Single(response.Headers.GetValues("x-trace-id"));
        Assert.Equal(traceId, responseTraceId);
        Assert.Matches("^[0-9a-f]{32}$", responseTraceId);
        Assert.False(response.Headers.Contains("traceparent"));
    }

    [Fact]
    public async Task ShortCircuitedResponse_IncludesTraceId()
    {
        await using var app = await CreateAppAsync(webApp =>
        {
            webApp.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            });
        });

        var response = await app.GetTestClient().GetAsync("/probe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var traceId = Assert.Single(response.Headers.GetValues("x-trace-id"));
        Assert.Matches("^[0-9a-f]{32}$", traceId);
        Assert.False(response.Headers.Contains("traceparent"));
    }

    [Fact]
    public async Task RequestWithTraceParent_ResponseUsesIncomingTraceId()
    {
        const string traceId = "11111111111111111111111111111111";
        await using var app = await CreateAppAsync(webApp => webApp.MapGet("/probe", Results.NoContent));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-2222222222222222-01");

        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(traceId, Assert.Single(response.Headers.GetValues("x-trace-id")));
        Assert.False(response.Headers.Contains("traceparent"));
    }

    private static async Task<WebApplication> CreateAppAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        app.UseMiddleware<TraceIdResponseHeaderMiddleware>();
        configure(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
