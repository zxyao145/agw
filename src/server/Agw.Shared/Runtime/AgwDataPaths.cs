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

    private AgwDataPaths(string root, string logsDirectory, string? legacyRoot, string userHomeDirectory)
    {
        _legacyRoot = legacyRoot;
        UserHomeDirectory = userHomeDirectory;
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
    /// Gets the absolute user home used to expand <c>~</c> in paths resolved for this Server instance.
    /// 获取本 Server 实例展开路径中 <c>~</c> 时使用的绝对用户目录。
    /// </summary>
    public string UserHomeDirectory { get; }

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
            usesDefaultRoot ? Path.GetFullPath(Path.Combine(userHome, "agw")) : null,
            userHome
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
    /// Creates missing data directories and verifies that the log directory accepts new files.
    /// New Unix directories are private to the current user; existing directories keep their permissions.
    /// 创建缺失的数据目录并验证日志目录可写入新文件。Unix 新建目录只允许当前用户访问，已有目录保留原有权限。
    /// </summary>
    public void EnsureCreated()
    {
        EnsureCreated(_ => { });
    }

    /// <inheritdoc cref="EnsureCreated()"/>
    /// <param name="onDirectory">Receives each directory before it is processed / 处理每个目录前接收该目录。</param>
    public void EnsureCreated(Action<string> onDirectory)
    {
        onDirectory(Root);
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
            onDirectory(directory);
            CreateDirectory(directory);
        }

        onDirectory(LogsDirectory);
        VerifyWritable(LogsDirectory);
    }

    private static void CreateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        // .NET 只为最后一级目录应用 mode，因此逐级创建缺失的上级目录。
        // 权限只作用于新建目录，已有目录保留 mode、ACL、属主和用户组。
        // .NET applies the mode only to the last segment, so missing ancestors are created one level at a time.
        // The mode applies only to new directories; existing ones keep their mode, ACL, owner, and group.
        var parent = Path.GetDirectoryName(directory);
        if (parent is not null && !Directory.Exists(parent))
            CreateDirectory(parent);

        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void VerifyWritable(string directory)
    {
        using var probe = new FileStream(
            Path.Combine(directory, $".agw-write-probe-{Guid.NewGuid():N}"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 0,
            FileOptions.DeleteOnClose
        );
        probe.WriteByte(0);
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
