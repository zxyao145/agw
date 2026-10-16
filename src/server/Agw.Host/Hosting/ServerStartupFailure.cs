using System.Text.Encodings.Web;
using System.Text.Json;

namespace Agw.Host.Hosting;

/// <summary>
/// Server startup stages reported by startup failure diagnostics.
/// 启动失败诊断中报告的 Server 启动阶段。
/// </summary>
internal enum ServerStartupStage
{
    Configuration,
    DataPaths,
    Directories,
    Logger,
    Deployment,
    Database,
    HostStart,
}

/// <summary>
/// Writes one structured stderr record for a failed Server startup.
/// 为启动失败的 Server 向 stderr 写入一条结构化诊断记录。
/// </summary>
internal static class ServerStartupFailure
{
    public const int ExitCode = 1;

    // 记录只写入 stderr，不嵌入 HTML，因此路径中的引号、& 和非 ASCII 字符按原样输出。
    // The record goes only to stderr and is never embedded in HTML, so quotes, & and non-ASCII path characters stay readable.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(
        TextWriter writer,
        TimeProvider timeProvider,
        ServerStartupStage stage,
        AgwHostProfile profile,
        string? path,
        Exception exception
    )
    {
        writer.WriteLine(
            JsonSerializer.Serialize(
                new ServerStartupFailureRecord(
                    "server-startup-failed",
                    timeProvider.GetUtcNow(),
                    stage.ToString(),
                    profile.ToString(),
                    Environment.ProcessId,
                    path,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message,
                    ExitCode
                ),
                SerializerOptions
            )
        );
        writer.Flush();
    }

    private sealed record ServerStartupFailureRecord(
        string Event,
        DateTimeOffset Timestamp,
        string Stage,
        string HostProfile,
        int ProcessId,
        string? Path,
        string ExceptionType,
        string Message,
        int ExitCode
    );
}
