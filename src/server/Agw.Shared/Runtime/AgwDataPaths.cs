using Agw.Shared.Utils;
using Microsoft.Extensions.Configuration;

namespace Agw.Shared.Runtime;

/// <summary>
/// Provides the canonical file-system locations used by an Agw Server instance.
/// </summary>
public sealed class AgwDataPaths
{
    private AgwDataPaths(string root, string logsDirectory)
    {
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
    /// Gets the root directory that contains persistent Server data, excluding logs.
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
    /// <param name="userHome">The user home directory used to construct the default <c>agw</c> data root.</param>
    /// <param name="configuredLogsDirectory">The separate log directory, defaulting to ./logs.</param>
    /// <returns>The canonical absolute paths for the Server data directories and files.</returns>
    public static AgwDataPaths Resolve(string? configuredRoot, string userHome, string? configuredLogsDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(configuredRoot) ? Path.Combine(userHome, "agw") : configuredRoot.Trim();

        var logsDirectory = string.IsNullOrWhiteSpace(configuredLogsDirectory)
            ? "./logs"
            : configuredLogsDirectory.Trim();
        return new AgwDataPaths(ResolvePath(root, userHome), ResolvePath(logsDirectory, userHome));
    }

    private static string ResolvePath(string root, string userHome)
    {
        return Path.GetFullPath(PathUtil.ExpandTilde(root, userHome));
    }

    /// <summary>
    /// Resolves Server data paths from the standard configuration chain's AgwDataDir key.
    /// The root is selected once at startup and defaults to ~/agw.
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
}
