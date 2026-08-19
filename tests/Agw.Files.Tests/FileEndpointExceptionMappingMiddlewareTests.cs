using System.Text.Json;
using Agw.Files.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

public class FileEndpointExceptionMappingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenFilesEndpointThrowsUnauthorizedAccess_ReturnsForbiddenError()
    {
        var context = CreateHttpContext("/api/files/read", "path=/workspace/file.txt");
        var middleware = CreateMiddleware(_ => throw new UnauthorizedAccessException("denied"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var payload = await ReadJsonResponseAsync(context);
        Assert.Equal("Access denied", payload.GetProperty("error").GetString());
        Assert.Equal("denied", payload.GetProperty("details").GetString());
    }

    [Fact]
    public async Task InvokeAsync_WhenFilesEndpointThrowsException_ReturnsEndpointFailureTemplate()
    {
        var context = CreateHttpContext("/api/files/search", "path=/workspace");
        var middleware = CreateMiddleware(_ => throw new IOException("disk unavailable"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        var payload = await ReadJsonResponseAsync(context);
        Assert.Equal("Failed to search directory", payload.GetProperty("error").GetString());
        Assert.Equal("disk unavailable", payload.GetProperty("details").GetString());
    }

    [Fact]
    public async Task InvokeAsync_WhenNonFilesEndpointThrowsException_Rethrows()
    {
        var context = CreateHttpContext("/api/projects", null);
        var middleware = CreateMiddleware(_ => throw new IOException("boom"));

        var exception = await Assert.ThrowsAsync<IOException>(() => middleware.InvokeAsync(context));

        Assert.Equal("boom", exception.Message);
    }

    private static FileEndpointExceptionMappingMiddleware CreateMiddleware(RequestDelegate next)
    {
        return new FileEndpointExceptionMappingMiddleware(
            next,
            NullLogger<FileEndpointExceptionMappingMiddleware>.Instance
        );
    }

    private static DefaultHttpContext CreateHttpContext(string path, string? queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (queryString != null)
        {
            context.Request.QueryString = new QueryString($"?{queryString}");
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonElement> ReadJsonResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }
}
