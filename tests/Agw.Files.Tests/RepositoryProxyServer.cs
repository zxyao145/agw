using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Agw.Files.Tests;

internal sealed class RepositoryProxyServer : IAsyncDisposable
{
    private readonly WebApplication _origin;
    private readonly WebApplication _proxy;
    private int _requests;

    private RepositoryProxyServer(WebApplication origin, WebApplication proxy)
    {
        _origin = origin;
        _proxy = proxy;
    }

    public string ProxyUrl => _proxy.Urls.Single();
    public int Requests => Volatile.Read(ref _requests);
    public const string RepositoryUrl = "http://repository.invalid/";

    public static async Task<RepositoryProxyServer> StartAsync(string repository, CancellationToken ct)
    {
        var origin = CreateBuilder().Build();
        origin.UseStaticFiles(
            new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(Path.Combine(repository, ".git")),
                ServeUnknownFileTypes = true,
                DefaultContentType = "application/octet-stream",
            }
        );
        await origin.StartAsync(ct);
        var builder = CreateBuilder();
        builder
            .Services.AddHttpClient("git-origin", client => client.BaseAddress = new Uri(origin.Urls.Single()))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });
        var proxy = builder.Build();
        var server = new RepositoryProxyServer(origin, proxy);
        proxy.Run(server.ForwardAsync);
        await proxy.StartAsync(ct);
        return server;
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        return builder;
    }

    private async Task ForwardAsync(HttpContext context)
    {
        Interlocked.Increment(ref _requests);
        var factory = _proxy.Services.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient("git-origin");
        using var response = await client.GetAsync(
            $"{context.Request.Path}{context.Request.QueryString}",
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted
        );
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _origin.DisposeAsync();
    }
}
