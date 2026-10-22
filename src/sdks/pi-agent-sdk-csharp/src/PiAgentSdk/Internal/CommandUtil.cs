using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PiAgentSdk.Internal;

/// <summary>Resolves the Pi CLI target without starting or probing a child process.</summary>
public static partial class CommandUtil
{
    /// <summary>
    /// 在子进程环境中查找 PiPathOverride 指定的命令或路径，省略时查找 pi。
    /// Finds the command or path from PiPathOverride, or pi when it is omitted, in the child-process environment.
    /// </summary>
    /// <param name="overridePath">显式指定的 Pi 命令或路径。An explicit Pi command or path.</param>
    /// <param name="environment">查找和子进程共用的完整环境。The complete environment shared by lookup and the child process.</param>
    /// <param name="cancellationToken">用于取消本次查找。Cancels this lookup.</param>
    /// <returns>找到的 Pi CLI，login shell 命中时包含其 PATH。The resolved Pi CLI, including the PATH of a login-shell match.</returns>
    /// <exception cref="InvalidOperationException">No Pi CLI target can be resolved.</exception>
    internal static async Task<CommandInfo> ResolvePiPathAsync(
        string? overridePath,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken = default
    )
    {
        var command = string.IsNullOrWhiteSpace(overridePath) ? "pi" : overridePath;
        CommandLookupOptions lookupOptions = new()
        {
            UseLoginShell = !OperatingSystem.IsWindows(),
            ShellTimeout = TimeSpan.FromSeconds(3),
            Environment = environment,
        };
        CommandInfo result = await FindAsync(command, lookupOptions, cancellationToken);

        if (result.IsInstalled)
        {
            return result;
        }

        throw new InvalidOperationException(
            $"Pi CLI '{command}' was not found. Install @earendil-works/pi-coding-agent or set PiAgentOptions.PiPathOverride."
                + (result.Error is null ? "" : $" {result.Error}")
        );
    }
}

/// <summary>
/// 命令的发现来源。
/// The source used to locate the command.
/// </summary>
public enum CommandSource
{
    /// <summary>调用方提供的显式路径。An explicit path supplied by the caller.</summary>
    ExplicitPath,

    /// <summary>当前环境的 PATH。The PATH of the supplied or current environment.</summary>
    Path,

    /// <summary>登录 shell 提供的 PATH。The PATH obtained from a login shell.</summary>
    LoginShell,
}

/// <summary>
/// 一次命令查找的结果；查找失败时可通过 Error 区分查询异常与正常未找到。
/// The result of a lookup; Error distinguishes a failed lookup from a normal miss.
/// </summary>
/// <param name="Command">原始命令名或路径。The original command name or path.</param>
/// <param name="Path">找到的绝对路径，未找到时为 null。The absolute path, or null if no match was found.</param>
/// <param name="Source">路径来源，未找到时为 null。The discovery source, or null if no match was found.</param>
/// <param name="Error">shell 查询失败或超时的信息，正常未找到时为 null。A shell failure or timeout diagnostic; null for a normal miss.</param>
public sealed record CommandInfo(string Command, string? Path, CommandSource? Source = null, string? Error = null)
{
    /// <summary>
    /// login shell 查找使用的绝对 PATH；null 表示保留输入环境的 PATH。
    /// Absolute PATH used by login-shell discovery; null preserves the input environment's PATH.
    /// </summary>
    public string? SearchPath { get; init; }

    /// <summary>
    /// 是否在本次搜索中找到可执行入口；不表示包管理器安装状态，也不保证程序能正常启动。
    /// Whether an executable entrypoint was found; this does not verify package installation or successful startup.
    /// </summary>
    public bool IsInstalled => Path is not null;
}

/// <summary>
/// 命令查找所用的环境及 shell 补查选项。
/// Environment and login-shell fallback options for command lookup.
/// </summary>
public sealed record CommandLookupOptions
{
    /// <summary>
    /// PATH 未命中时是否尝试读取 Unix 登录 shell 的 PATH，默认启用；此操作可能运行用户的 shell 启动配置。
    /// Whether to read a Unix login shell's PATH after a miss; enabled by default and may run user startup configuration.
    /// </summary>
    public bool UseLoginShell { get; init; } = true;

    /// <summary>
    /// shell 查询超时，默认三秒且必须为正值；异步清理最多额外等待一秒。
    /// The shell lookup timeout, positive and three seconds by default; asynchronous cleanup may wait one additional second.
    /// </summary>
    public TimeSpan ShellTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 完整的环境变量集合，非增量覆盖；null 表示使用当前进程环境。
    /// A complete environment, not a set of overrides; null uses the current process environment.
    /// </summary>
    /// <remarks>
    /// 每次查询都会复制该集合，调用方应避免在复制期间修改它。
    /// Each lookup copies this collection; callers should avoid modifying it during the copy.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// 按 Windows、Linux 和 macOS 的平台规则查找命令，不执行目标命令，不缓存查询结果。
/// Finds commands using Windows, Linux, and macOS platform rules without executing the target or caching results.
/// </summary>
public static partial class CommandUtil
{
    /// <summary>
    /// 查找命令名或显式路径，返回可发现状态、绝对路径、来源及 shell 查询错误。
    /// Finds a command name or explicit path and returns its discovery status, absolute path, source, and shell errors.
    /// </summary>
    /// <param name="command">
    /// 单个命令名或文件路径，不含命令行参数；按字面值处理，不展开变量、波浪号或 shell 表达式。
    /// A single command name or file path without arguments; variables, tildes, and shell expressions are not expanded.
    /// </param>
    /// <param name="options">查询选项，null 使用默认值。Lookup options, or null to use the defaults.</param>
    /// <param name="cancellationToken">用于取消本次查询。Cancels this lookup.</param>
    /// <returns>
    /// 查询结果；未找到且 Error 非 null 时，表示 shell 补查未能完整完成。
    /// The lookup result; a miss with a non-null Error means shell fallback did not complete successfully.
    /// </returns>
    /// <exception cref="ArgumentException">命令为空白或含 NUL 字符。The command is blank or contains a NUL character.</exception>
    /// <exception cref="ArgumentOutOfRangeException">超时配置无效。The timeout configuration is invalid.</exception>
    /// <exception cref="OperationCanceledException">调用方取消了查询。The caller canceled the lookup.</exception>
    public static async Task<CommandInfo> FindAsync(
        string command,
        CommandLookupOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Contains('\0'))
            throw new ArgumentException("Command must not contain a null character.", nameof(command));
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        if (options.ShellTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Shell timeout must be positive.");

        // 固定本次查询的环境和初始工作目录，供后续路径解析使用。
        // Capture the environment and initial working directory for this lookup's path resolution.
        var environment = SnapshotEnvironment(options.Environment);
        var directory = Environment.CurrentDirectory;
        var extensions = environment
            .GetValueOrDefault("PATHEXT", ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // 显式路径只检查指定位置，失败后不会转而搜索 PATH 中的同名命令。
        // An explicit path checks only that location; a miss does not fall back to a same-named command on PATH.
        if (
            Path.IsPathRooted(command)
            || command.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
        )
        {
            var explicitPath = FindExecutable(command, directory, extensions);
            return new(command, explicitPath, explicitPath is null ? null : CommandSource.ExplicitPath);
        }

        if (
            FindOnPath(command, environment.GetValueOrDefault("PATH"), directory, extensions, cancellationToken) is
            { } path
        )
            return new(command, path, CommandSource.Path);
        // shell 补查仅用于 Unix 上 PATH 未命中的情况，Windows 使用 PATH 与 PATHEXT。
        // Shell fallback applies only to Unix PATH misses; Windows uses PATH and PATHEXT.
        if (
            !options.UseLoginShell
            || OperatingSystem.IsWindows()
            || !environment.TryGetValue("SHELL", out var shell)
            || string.IsNullOrWhiteSpace(shell)
        )
            return new(command, null);
        // 固定查询脚本使用 POSIX shell 语法，因此仅接受这些兼容的 shell。
        // The fixed query script uses POSIX shell syntax, so only these compatible shells are accepted.
        if (Path.GetFileName(shell) is not ("sh" or "bash" or "zsh" or "dash" or "ksh"))
            return new(command, null, Error: $"Login shell is not supported: {Path.GetFileName(shell)}");
        if (!Path.IsPathFullyQualified(shell))
        {
            shell = FindOnPath(shell, environment.GetValueOrDefault("PATH"), directory, extensions, cancellationToken);
            if (shell is null)
                return new(command, null, Error: "Login shell executable was not found.");
        }

        var (shellPath, error) = await ReadLoginShellPathAsync(
                shell,
                environment,
                options.ShellTimeout,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (shellPath is null)
            return new(command, null, Error: error);
        // 相对 PATH 项按 shell 工作目录转为绝对路径，供查找和子进程共同使用。
        // Resolve relative PATH entries against the shell directory for both lookup and child execution.
        var searchPath = string.Join(
            Path.PathSeparator,
            shellPath
                .Value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => Path.GetFullPath(entry, shellPath.WorkingDirectory))
        );
        path = FindOnPath(command, searchPath, shellPath.WorkingDirectory, extensions, cancellationToken);
        return new(command, path, path is null ? null : CommandSource.LoginShell)
        {
            SearchPath = path is null ? null : searchPath,
        };
    }

    /// <summary>
    /// 按 PATH 项顺序返回首个匹配；忽略空项，搜索当前目录需显式使用 "."。
    /// Returns the first match in PATH order; empty entries are ignored, so use "." explicitly for the current directory.
    /// </summary>
    private static string? FindOnPath(
        string command,
        string? searchPath,
        string directory,
        string[] extensions,
        CancellationToken token
    )
    {
        if (string.IsNullOrEmpty(searchPath))
            return null;
        foreach (var entry in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            var prefix = OperatingSystem.IsWindows() ? entry.Trim('"') : entry;
            if (prefix.Length == 0)
                continue;
            if (FindExecutable(Path.Combine(prefix, command), directory, extensions) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>
    /// 将候选路径转为绝对路径并按平台规则检查，保留符号链接入口名称。
    /// Resolves a candidate to an absolute path and checks platform rules while preserving the symbolic-link entrypoint name.
    /// </summary>
    private static string? FindExecutable(string candidate, string directory, string[] extensions)
    {
        try
        {
            candidate = Path.GetFullPath(candidate, directory);
            if (!OperatingSystem.IsWindows())
                return File.Exists(candidate) && CheckUnixAccess(candidate, 1) == 0 ? candidate : null;

            // 显式使用四种常见可执行扩展名时，即使 PATHEXT 未列出也可匹配。
            // The four common executable extensions match explicitly even when PATHEXT omits them.
            var extension = Path.GetExtension(candidate);
            if (
                (
                    extension.ToUpperInvariant() is ".EXE" or ".COM" or ".BAT" or ".CMD"
                    || extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                ) && File.Exists(candidate)
            )
                return candidate;
            foreach (var suffix in extensions)
                if (suffix.StartsWith('.') && File.Exists(candidate + suffix))
                    return candidate + suffix;
            return null;
        }
        catch (Exception error)
            when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // X_OK = 1，按进程真实用户及组身份检查执行访问权限；返回零表示允许执行访问。
    // X_OK = 1 checks execution access for the process's real user/group identity; zero means access is allowed.
    [DllImport("libc", EntryPoint = "access")]
    private static extern int CheckUnixAccess([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    /// <summary>
    /// 复制提供的或当前进程的环境，Windows 环境变量名不区分大小写。
    /// Copies the supplied or current process environment, using case-insensitive variable names on Windows.
    /// </summary>
    private static Dictionary<string, string> SnapshotEnvironment(IReadOnlyDictionary<string, string>? environment)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return environment is not null
            ? new(environment, comparer)
            : Environment
                .GetEnvironmentVariables()
                .Cast<DictionaryEntry>()
                .ToDictionary(x => (string)x.Key, x => (string)x.Value!, comparer);
    }

    // 同时保留 shell 的 PATH 和工作目录，以正确处理相对路径。
    // Keep both the shell's PATH and working directory to resolve relative entries correctly.
    private sealed record ShellPath(string Value, string WorkingDirectory);

    /// <summary>
    /// 在超时和取消控制下读取登录 shell 的 PATH 与工作目录，将查询失败转换为诊断信息。
    /// Reads a login shell's PATH and working directory with timeout and cancellation, returning diagnostics on lookup failure.
    /// </summary>
    private static async Task<(ShellPath? Path, string? Error)> ReadLoginShellPathAsync(
        string shell,
        Dictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken token
    )
    {
        // 输入命令不会进入脚本；脚本只输出以 NUL 分隔的 PATH 和工作目录。
        // The requested command never enters the script; it only emits NUL-delimited PATH and working-directory fields.
        const string script = "command -p printf '\\0%s\\0%s\\0' \"$PATH\" \"$(command -p pwd -P)\"";
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // 使用交互式登录 shell，使其读取相应的用户启动配置。
        // Use an interactive login shell so it reads the corresponding user startup configuration.
        start.ArgumentList.Add("-ilc");
        start.ArgumentList.Add(script);
        start.Environment.Clear();
        foreach (var (key, value) in environment)
            start.Environment[key] = value;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return (null, $"Cannot start login shell: {error.Message}");
        }

        var frame = ReadShellFrameAsync(process.StandardOutput, deadline.Token);
        // 持续排空标准错误，避免缓冲区填满后阻塞 shell，同时不积累输出。
        // Drain stderr without retaining it so a full pipe cannot block the shell.
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        try
        {
            // 读取到完整帧即可返回；启动配置中的后台进程可能持续持有标准输出管道。
            // A complete frame is enough; a startup script's background process may keep stdout open.
            var result = await frame.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (result is not null && Path.IsPathFullyQualified(result.WorkingDirectory))
                return (result, null);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return (null, $"Login shell exited with code {process.ExitCode} without returning a valid search path.");
        }
        catch (OperationCanceledException)
        {
            // 调用方取消向外传播；内部超时则作为诊断信息返回。
            // Propagate caller cancellation; report an internal timeout as a diagnostic.
            token.ThrowIfCancellationRequested();
            return (null, "Login shell lookup timed out.");
        }
        catch (IOException error)
        {
            return (null, $"Cannot read login shell search path: {error.Message}");
        }
        finally
        {
            // 尽力结束仍存活的辅助进程树；不管理用户启动配置中已脱离父进程的服务。
            // Attempt to stop the live helper process tree; detached services started by user configuration are not managed.
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }

            await deadline.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(frame, stderr, process.WaitForExitAsync(CancellationToken.None))
                    .WaitAsync(TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException) { }
        }
    }

    /// <summary>
    /// 忽略首个 NUL 前的启动提示，读取 NUL + PATH + NUL + 工作目录 + NUL 格式的完整帧。
    /// Ignores startup text before the first NUL and reads a complete NUL + PATH + NUL + working directory + NUL frame.
    /// </summary>
    private static async Task<ShellPath?> ReadShellFrameAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        var value = new StringBuilder();
        string? searchPath = null;
        var started = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0)
                return null;
            for (var i = 0; i < count; i++)
            {
                var character = buffer[i];
                if (character == '\0')
                {
                    if (!started)
                    {
                        started = true;
                        continue;
                    }

                    if (searchPath is not null)
                        return new(searchPath, value.ToString());
                    searchPath = value.ToString();
                    value.Clear();
                }
                else if (started)
                {
                    // 限制每个字段的长度，避免异常输出导致内存无限增长。
                    // Bound each field's length to prevent unbounded memory growth from unexpected output.
                    if (value.Length >= 64 * 1024)
                        throw new InvalidDataException("Shell search path exceeds 64 KiB.");
                    value.Append(character);
                }
            }
        }
    }
}
