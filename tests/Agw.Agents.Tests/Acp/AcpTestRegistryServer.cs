using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Tests.Acp;

internal sealed class AcpTestRegistryServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string? ContentType)> _files = new();
    private string _registry = """{"version":"1.0.0","agents":[]}""";
    private int _requests;

    private AcpTestRegistryServer(WebApplication app)
    {
        _app = app;
        app.MapGet(
            "/registry.json",
            (HttpContext context) =>
            {
                Interlocked.Increment(ref _requests);
                var json = Volatile.Read(ref _registry);
                var etag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))}\"";
                context.Response.Headers.ETag = etag;
                return context.Request.Headers.IfNoneMatch.Contains(etag)
                    ? Results.StatusCode(304)
                    : Results.Text(json, "application/json");
            }
        );
        app.MapGet("/redirect", () => Results.Redirect("http://example.com"));
        app.MapGet(
            "/slow-release",
            async (HttpContext context) =>
            {
                await context.Response.StartAsync(context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
        );
        app.MapGet(
            "/{**path}",
            (HttpContext context) =>
                _files.TryGetValue(context.Request.Path.Value!, out var file)
                    ? Results.Bytes(file.Bytes, file.ContentType)
                    : Results.NotFound()
        );
    }

    public int RegistryRequestCount => Volatile.Read(ref _requests);
    public Uri RegistryUrl =>
        new(
            new Uri(
                _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()
            ),
            "/registry.json"
        );

    public static async Task<AcpTestRegistryServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var server = new AcpTestRegistryServer(builder.Build());
        await server._app.StartAsync();
        return server;
    }

    public void SetRegistry(string json) => Volatile.Write(ref _registry, json);

    public Uri AddFile(string path, byte[] content, string? contentType = null)
    {
        var uri = new Uri(RegistryUrl, path);
        _files[uri.AbsolutePath] = (content, contentType);
        return uri;
    }

    public Task StopAsync() => _app.StopAsync();

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
