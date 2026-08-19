using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Agw.Files.Api;

public sealed class FileEndpointExceptionMappingMiddleware
{
    public const string ResolvedPathItemKey = "Agw.Files.ResolvedPath";

    private static readonly IReadOnlyDictionary<string, FileEndpointExceptionTemplate> Templates = new Dictionary<
        string,
        FileEndpointExceptionTemplate
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["list"] = new("reading directory", "Failed to read directory"),
        ["read"] = new("reading file", "Failed to read file"),
        ["diff"] = new("getting git diff", "Failed to get git diff"),
        ["delete"] = new("deleting", "Failed to delete"),
        ["reset"] = new("resetting file", "Failed to reset file"),
        ["search"] = new("searching directory", "Failed to search directory"),
    };

    private static readonly FileEndpointExceptionTemplate DefaultTemplate = new(
        "processing file request",
        "Failed to process file request"
    );

    private readonly RequestDelegate _next;
    private readonly ILogger<FileEndpointExceptionMappingMiddleware> _logger;

    public FileEndpointExceptionMappingMiddleware(
        RequestDelegate next,
        ILogger<FileEndpointExceptionMappingMiddleware> logger
    )
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex) when (ShouldMapException(context))
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            await WriteMappedExceptionAsync(context, ex);
        }
    }

    private static bool ShouldMapException(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments("/api/files", StringComparison.OrdinalIgnoreCase);
    }

    private async Task WriteMappedExceptionAsync(HttpContext context, Exception exception)
    {
        var template = ResolveTemplate(context.Request.Path);
        var path = ResolvePathForLogging(context);

        if (exception is UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Access denied {Operation}: {Path}", template.Operation, path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Access denied", details = exception.Message });
            return;
        }

        _logger.LogError(exception, "Error {Operation}: {Path}", template.Operation, path);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = template.FailureMessage, details = exception.Message });
    }

    private static FileEndpointExceptionTemplate ResolveTemplate(PathString path)
    {
        if (!path.StartsWithSegments("/api/files", out var remaining))
        {
            return DefaultTemplate;
        }

        var endpointName = remaining
            .Value?.Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return endpointName != null && Templates.TryGetValue(endpointName, out var template)
            ? template
            : DefaultTemplate;
    }

    private static string ResolvePathForLogging(HttpContext context)
    {
        if (
            context.Items.TryGetValue(ResolvedPathItemKey, out var resolvedPath)
            && resolvedPath is string value
            && !string.IsNullOrWhiteSpace(value)
        )
        {
            return value;
        }

        return context.Request.Query.TryGetValue("path", out var requestedPath)
            ? requestedPath.ToString()
            : string.Empty;
    }

    private sealed record FileEndpointExceptionTemplate(string Operation, string FailureMessage);
}
