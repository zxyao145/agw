using System.Diagnostics;
using System.Text.Json;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.Configuration;

namespace Agw.Shared.Runtime;

/// <summary>
/// Provides the canonical file-system locations used by an Agw Server instance.
/// </summary>
public sealed class AgwDataPaths
{
    private readonly string? _legacyRoot;

    private AgwDataPaths(string root, string logsDirectory, string? legacyRoot)
    {
        _legacyRoot = legacyRoot;
        Root = root;
        DatabaseFile = Path.Combine(root, "database", "agw.db");
        SkillsDirectory = Path.Combine(root, "skills");
        LogsDirectory = logsDirectory;
        TempDirectory = Path.Combine(root, "temp");
        KeysDirectory = Path.Combine(root, "keys");
        RuntimeDirectory = Path.Combine(root, "runtime");
        ServerRuntimeFile = Path.Combine(RuntimeDirectory, "server.json");
    }

    /// <summary>
    /// Gets the persistent Server data root.
    /// 获取 Server 的持久数据根目录。
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// Gets the path to the persisted Server setup and initialization state.
    /// </summary>
    /// <summary>
    /// Gets the path to the default SQLite database file.
    /// </summary>
    public string DatabaseFile { get; }

    /// <summary>
    /// Gets the directory that stores validated and extracted skills.
    /// </summary>
    public string SkillsDirectory { get; }

    /// <summary>
    /// Gets the independently configured directory that stores Server log files.
    /// </summary>
    public string LogsDirectory { get; }

    /// <summary>
    /// Gets the directory for temporary files managed by the Server.
    /// </summary>
    public string TempDirectory { get; }

    /// <summary>
    /// Gets the directory that stores ASP.NET Core Data Protection keys.
    /// </summary>
    public string KeysDirectory { get; }

    /// <summary>
    /// Gets the directory that stores transient metadata for currently running Agw processes.
    /// Its contents are used for local process discovery and may be removed when those processes stop.
    /// </summary>
    public string RuntimeDirectory { get; }

    /// <summary>
    /// Gets the path to the current Server runtime descriptor. The descriptor publishes the live
    /// process ID, listening endpoint, and compatibility information for local clients such as Desktop.
    /// It is not persistent Server configuration and does not contain credentials.
    /// </summary>
    public string ServerRuntimeFile { get; }

    /// <summary>
    /// Resolves all Server data paths from an optional configured root directory.
    /// </summary>
    /// <param name="configuredRoot">The configured data root, or <see langword="null"/> to use the default.</param>
    /// <param name="userHome">User home for tilde expansion and legacy data / 用于展开波浪号和查找原数据的用户目录。</param>
    /// <param name="configuredLogsDirectory">Log directory, defaulting to Root/logs / 日志目录，默认使用 Root/logs。</param>
    /// <param name="localApplicationDataDirectory">System LocalApplicationData directory / 系统的 LocalApplicationData 目录。</param>
    /// <returns>The canonical absolute paths for the Server data directories and files.</returns>
    public static AgwDataPaths Resolve(
        string? configuredRoot,
        string userHome,
        string? configuredLogsDirectory = null,
        string? localApplicationDataDirectory = null
    )
    {
        var usesDefaultRoot = string.IsNullOrWhiteSpace(configuredRoot);
        var root = usesDefaultRoot ? ResolveDefaultRoot(localApplicationDataDirectory) : configuredRoot!.Trim();
        root = ResolvePath(root, userHome);

        var logsDirectory = string.IsNullOrWhiteSpace(configuredLogsDirectory)
            ? Path.Combine(root, "logs")
            : configuredLogsDirectory.Trim();
        return new AgwDataPaths(
            root,
            ResolvePath(logsDirectory, userHome),
            usesDefaultRoot ? Path.GetFullPath(Path.Combine(userHome, "agw")) : null
        );
    }

    private static string ResolvePath(string root, string userHome)
    {
        return Path.GetFullPath(PathUtil.ExpandTilde(root, userHome));
    }

    /// <summary>
    /// Resolves Server data paths from the standard configuration chain's AgwDataDir key.
    /// The root is selected once at startup from the system's LocalApplicationData directory.
    /// 启动时从系统的 LocalApplicationData 目录确定默认根目录。
    /// </summary>
    public static AgwDataPaths ResolveFromConfiguration(IConfiguration configuration)
    {
        var configuredRoot = configuration["AgwDataDir"];
        return Resolve(
            configuredRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            configuration["AgwLogDir"]
        );
    }

    /// <summary>
    /// Creates the required data directories and restricts them to the current user on Unix systems.
    /// </summary>
    public void EnsureCreated()
    {
        MigrateLegacyData();
        var directories = new[]
        {
            Root,
            Path.GetDirectoryName(DatabaseFile)!,
            SkillsDirectory,
            LogsDirectory,
            TempDirectory,
            KeysDirectory,
            RuntimeDirectory,
        };
        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }
        }
    }

    private static string ResolveDefaultRoot(string? localApplicationDataDirectory)
    {
        var directory =
            localApplicationDataDirectory
            ?? Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify
            );
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Unable to resolve the system's LocalApplicationData directory."
            );

        return Path.Combine(directory, OperatingSystem.IsLinux() ? "agw" : "Agw");
    }

    private void MigrateLegacyData()
    {
        if (
            _legacyRoot is null
            || !Directory.Exists(_legacyRoot)
            || !Directory.EnumerateFileSystemEntries(_legacyRoot).Any()
        )
            return;

        if (Directory.Exists(Root))
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Both data directories exist: '{_legacyRoot}' and '{Root}'. Set AgwDataDir to select the data to use."
            );

        if (new DirectoryInfo(_legacyRoot).LinkTarget is not null)
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Set AgwDataDir explicitly to use the symbolic-link data directory '{_legacyRoot}'."
            );

        EnsureLegacyServerStopped();
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        Directory.Move(_legacyRoot, Root);
    }

    private void EnsureLegacyServerStopped()
    {
        var runtimeFile = Path.Combine(_legacyRoot!, "runtime", "server.json");
        if (!File.Exists(runtimeFile))
            return;

        using var descriptor = JsonDocument.Parse(File.ReadAllText(runtimeFile));
        var pid = descriptor.RootElement.GetProperty("pid").GetInt32();
        var startedAt = descriptor.RootElement.GetProperty("startedAt").GetDateTimeOffset();
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            if (!process.HasExited && new DateTimeOffset(process.StartTime) <= startedAt)
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Stop the Server using '{_legacyRoot}' before starting the data directory migration."
                );
        }
    }
}
