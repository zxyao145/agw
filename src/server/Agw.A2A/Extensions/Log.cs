using Microsoft.Extensions.Logging;

namespace Agw.A2A.Extensions;

internal static partial class Log
{
    [LoggerMessage(3, LogLevel.Error, "Background event processing failed for task {TaskId}")]
    internal static partial void BackgroundEventProcessingFailed(
        this ILogger logger,
        Exception exception,
        string TaskId
    );
}
