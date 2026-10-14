using System.Globalization;
using Agw.Host.Hosting;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Serilog;
using Serilog.Core;
using Serilog.Enrichers.OpenTelemetry;

namespace Agw.Host.Logging;

/// <summary>
/// Creates the Server logger from the effective Host configuration.
/// Built-in file sinks are identified by named sections and always write to <see cref="AgwDataPaths.LogsDirectory"/>.
/// 根据 Host 的有效配置创建 Server logger。内置 File sink 通过命名节点识别，并统一写入日志目录。
/// </summary>
public static class AgwLoggerFactory
{
    private const string WriteToSection = "Serilog:WriteTo";
    private const string FilesSection = "Serilog:WriteTo:Files";
    private static readonly string[] BuiltInFileSinks = ["Info", "Error"];

    public static Logger CreateLogger(
        ConfigurationManager configuration,
        AgwDataPaths dataPaths,
        AgwHostProfile profile
    )
    {
        configuration.AddInMemoryCollection(ResolveBuiltInFilePaths(configuration, dataPaths.LogsDirectory));
        return new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            // Keep framework protocol errors visible for configuration and callback
            // diagnostics; Agw.Auth.Oidc also emits bounded stage/category details.
            .MinimumLevel.Override(
                "Microsoft.AspNetCore.Authentication.OpenIdConnect",
                Serilog.Events.LogEventLevel.Error
            )
            .MinimumLevel.Override("Microsoft.AspNetCore.Authentication.OAuth", Serilog.Events.LogEventLevel.Error)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.WithOpenTelemetryTraceId()
            .Enrich.WithOpenTelemetrySpanId()
            .WriteTo.Async(sink =>
                sink.File(
                    Path.Combine(dataPaths.LogsDirectory, $"application-{profile.ToString().ToLowerInvariant()}-.log"),
                    rollingInterval: RollingInterval.Hour,
                    retainedFileCountLimit: 30,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1)
                )
            )
            .CreateLogger();
    }

    private static Dictionary<string, string?> ResolveBuiltInFilePaths(
        IConfiguration configuration,
        string logsDirectory
    )
    {
        RejectIndexedSections(configuration.GetSection(WriteToSection));
        var files = configuration.GetSection(FilesSection);
        RequireName(files, "Async");
        var sinks = files.GetSection("Args:configure");
        RejectIndexedSections(sinks);

        var paths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in BuiltInFileSinks)
        {
            var sink = sinks.GetSection(name);
            RequireName(sink, "File");
            var path = sink.GetSection("Args:path");
            paths[path.Path] = Path.Combine(logsDirectory, RequireFileName(path));
        }

        return paths;
    }

    private static void RejectIndexedSections(IConfigurationSection section)
    {
        foreach (var child in section.GetChildren())
        {
            if (int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Configuration key '{child.Path}' uses an array index. Move it to a named section under '{section.Path}', such as '{WriteToSection}:Console', '{FilesSection}', or '{FilesSection}:Args:configure:Info'."
                );
        }
    }

    private static void RequireName(IConfigurationSection section, string expected)
    {
        if (!string.Equals(section["Name"], expected, StringComparison.Ordinal))
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Configuration key '{section.Path}:Name' must be '{expected}'."
            );
    }

    private static string RequireFileName(IConfigurationSection section)
    {
        var fileName = section.Value;
        if (
            string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || Path.IsPathRooted(fileName)
            || fileName.IndexOfAny(['/', '\\']) >= 0
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        )
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Configuration key '{section.Path}' must be a file name pattern such as 'agw-info-.log'. Configure the directory with AgwLogDir."
            );

        return fileName;
    }
}
