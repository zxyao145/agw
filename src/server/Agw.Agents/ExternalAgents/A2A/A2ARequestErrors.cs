using System.Net.Sockets;
using System.Text.Json;
using global::A2A;
using Agw.Shared.Exceptions;

namespace Agw.Agents.ExternalAgents.A2A;

public static class A2ARequestErrors
{
    public static bool IsRequestException(Exception exception) =>
        exception
            is AgwException
                or A2AException
                or HttpRequestException
                or IOException
                or JsonException
                or OperationCanceledException;

    public static AgwException? PreserveKnownError(Exception exception, Exception cause)
    {
        var known = exception as AgwException ?? exception.GetBaseException() as AgwException;
        return known is null ? null : new AgwException(known.Code, known.Message, known.StatusCode, cause);
    }

    public static HttpRequestException? FindHttpError(Exception exception) =>
        exception as HttpRequestException ?? exception.InnerException as HttpRequestException;

    // 诊断保留异常类型、错误码和堆栈。
    // Diagnostics retain exception types, error codes and stack traces.
    public static Exception SafeException(Exception exception) =>
        exception is DiagnosticException ? exception : new DiagnosticException(exception);

    private sealed class DiagnosticException : Exception
    {
        public DiagnosticException(Exception cause)
            : base(
                $"{cause.GetType().FullName}{ErrorDetails(cause)}",
                cause.InnerException is { } inner ? SafeException(inner) : null
            )
        {
            StackTrace = cause.StackTrace;
        }

        public override string? StackTrace { get; }

        private static string ErrorDetails(Exception cause) =>
            cause switch
            {
                AgwException error => $" ({error.Code})",
                A2AException error => $" ({error.ErrorCode})",
                HttpRequestException error => $" ({error.HttpRequestError}, HTTP {(int?)error.StatusCode})",
                SocketException error => $" ({error.SocketErrorCode})",
                JsonException error => $" (line {error.LineNumber}, byte {error.BytePositionInLine})",
                _ => "",
            };
    }
}
