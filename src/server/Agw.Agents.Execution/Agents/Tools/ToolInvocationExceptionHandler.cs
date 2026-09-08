using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Files.Exceptions;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Tools;

/// <summary>
/// Converts Tool invocation failures into model-visible results while preserving caller cancellation.
/// </summary>
internal sealed class ToolInvocationExceptionHandler
{
    private readonly ILogger<ToolInvocationExceptionHandler> _logger;

    public ToolInvocationExceptionHandler(ILogger<ToolInvocationExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await context.Function.InvokeAsync(context.Arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = CreateResult(exception);
            LogFailure(context, exception, result);
            Activity.Current?.SetStatus(ActivityStatusCode.Error, result.Message);
            Activity.Current?.AddEvent(
                new ActivityEvent(
                    "agw.tool.error",
                    tags: new ActivityTagsCollection
                    {
                        { "agw.tool.name", context.Function.Name },
                        { "agw.tool.error.code", result.Code },
                    }
                )
            );
            return result;
        }
    }

    internal static bool IsErrorResult(object? result)
    {
        if (result is ToolExecutionErrorResult { IsError: true })
        {
            return true;
        }

        return result is JsonElement { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("isError", out var isError)
            && isError.ValueKind == JsonValueKind.True;
    }

    private static ToolExecutionErrorResult CreateResult(Exception exception)
    {
        return exception switch
        {
            AgwException agwException => new ToolExecutionErrorResult(
                IsError: true,
                Code: agwException.Code,
                Message: agwException.Message
            ),
            AgwFilesException filesException => new ToolExecutionErrorResult(
                IsError: true,
                Code: filesException.Code,
                Message: filesException.Message
            ),
            _ => new ToolExecutionErrorResult(
                IsError: true,
                Code: ErrorCodes.ToolExecutionFailed.Code,
                Message: ErrorCodes.ToolExecutionFailed.Message
            ),
        };
    }

    private void LogFailure(FunctionInvocationContext context, Exception exception, ToolExecutionErrorResult result)
    {
        var callId = context.CallContent?.CallId;
        if (exception is AgwException or AgwFilesException)
        {
            _logger.LogWarning(
                exception,
                "Tool invocation failed with a handled error. ToolName={ToolName} CallId={CallId} ErrorCode={ErrorCode}",
                context.Function.Name,
                callId,
                result.Code
            );
            return;
        }

        _logger.LogError(
            exception,
            "Tool invocation failed with an unhandled error. ToolName={ToolName} CallId={CallId} ErrorCode={ErrorCode}",
            context.Function.Name,
            callId,
            result.Code
        );
    }
}

internal sealed record ToolExecutionErrorResult(
    [property: JsonPropertyName("isError")] bool IsError,
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message
);
