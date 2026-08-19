using Agw.Shared.Exceptions;
using Bens.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Agw.Shared.Results;

public sealed class AgwApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AgwApiExceptionMiddleware> _logger;

    public AgwApiExceptionMiddleware(RequestDelegate next, ILogger<AgwApiExceptionMiddleware> logger)
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
        catch (AgwException ex) when (!context.Response.HasStarted)
        {
            _logger.LogWarning(ex, "Mapped AgwException to API result.");
            await ApiResult.Fail(ex.Code, ex.Message, (int)ex.StatusCode).ExecuteAsync(context);
        }
    }
}
