using System.Diagnostics;
using System.Text;
using Agw.Shared.Exceptions;

namespace Agw.Tools.Impl.ContextualTools.Shell;

internal static class LoginShellPathReader
{
    private const int MaxFieldLengthKiB = 64;
    private const int MaxFieldLength = MaxFieldLengthKiB * 1024;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public static bool HasLoginShell(IReadOnlyDictionary<string, string> environment) =>
        environment.TryGetValue("SHELL", out var shell)
        && !string.IsNullOrWhiteSpace(shell)
        && Path.GetFileName(shell) is "sh" or "bash" or "zsh" or "dash" or "ksh";

    /// <summary>
    /// 在指定时限内读取 login shell 的 PATH，并展开当前用户的主目录简写。
    /// Reads the login shell PATH within the time limit and expands the current user's home directory shorthand.
    /// </summary>
    public static async Task<string> ReadAsync(
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = CreateStartInfo(environment);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AgwException(
                ErrorCodes.ShellLoginPathUnavailable,
                $"Cannot start login shell: {error.Message}",
                error
            );
        }

        var frame = ReadFrameAsync(process.StandardOutput, deadline.Token);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        var exit = process.WaitForExitAsync();
        try
        {
            return await ReadSearchPathAsync(
                    process,
                    frame,
                    exit,
                    environment.GetValueOrDefault("HOME"),
                    deadline.Token
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgwException(ErrorCodes.ShellLoginPathUnavailable, "Login shell lookup timed out.", error);
        }
        catch (IOException error)
        {
            throw new AgwException(
                ErrorCodes.ShellLoginPathUnavailable,
                $"Cannot read login shell search path: {error.Message}",
                error
            );
        }
        finally
        {
            await StopLookupAsync(process, deadline, Task.WhenAll(frame, stderr, exit)).ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo CreateStartInfo(IReadOnlyDictionary<string, string> environment)
    {
        var shell = environment["SHELL"];
        if (!Path.IsPathFullyQualified(shell))
            throw new AgwException(ErrorCodes.ShellLoginPathUnavailable, $"Login shell path is not absolute: {shell}");

        const string script = """
            command -p printf '\0%s\0' "$PATH"
            """;
        var startInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // 关闭 job control，让调用方保留终端的前台进程组。
        // Disable job control so the caller retains the terminal foreground process group.
        startInfo.ArgumentList.Add("-il");
        startInfo.ArgumentList.Add("+m");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        startInfo.Environment.Clear();
        foreach (var (key, value) in environment)
            startInfo.Environment[key] = value;

        return startInfo;
    }

    private static async Task<string> ReadSearchPathAsync(
        Process process,
        Task<string?> frame,
        Task exit,
        string? homeDirectory,
        CancellationToken cancellationToken
    )
    {
        await Task.WhenAny(frame, exit).WaitAsync(cancellationToken).ConfigureAwait(false);

        // shell 退出后，后台进程仍可能持有输出管道。限制读取剩余数据的等待时间。
        // Background processes may keep stdout open after the shell exits. Bound the wait for buffered output.
        string? searchPath;
        try
        {
            searchPath = await frame.WaitAsync(CleanupTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            searchPath = null;
        }

        if (searchPath is null)
        {
            await exit.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new AgwException(
                ErrorCodes.ShellLoginPathUnavailable,
                $"Login shell exited with code {process.ExitCode} without returning a valid search path."
            );
        }

        var entries = searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
            throw new AgwException(ErrorCodes.ShellLoginPathUnavailable, "Login shell returned an empty search path.");

        return string.Join(Path.PathSeparator, entries.Select(entry => NormalizeEntry(entry, homeDirectory)));
    }

    private static string NormalizeEntry(string entry, string? homeDirectory) =>
        !string.IsNullOrEmpty(homeDirectory) && (entry == "~" || entry.StartsWith("~/", StringComparison.Ordinal))
            ? homeDirectory + entry[1..]
            : entry;

    private static async Task StopLookupAsync(Process process, CancellationTokenSource deadline, Task lookup)
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);

        await deadline.CancelAsync().ConfigureAwait(false);
        await lookup.WaitAsync(CleanupTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static async Task<string?> ReadFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var field = new StringBuilder();
        var started = false;

        // 跳过第一个 NUL 之前的启动输出，然后读取长度受限的 PATH。
        // Skip startup output before the first NUL, then read the bounded PATH field.
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return null;

            for (var i = 0; i < count; i++)
            {
                var character = buffer[i];
                if (character == '\0')
                {
                    if (started)
                        return field.ToString();

                    started = true;
                }
                else if (started)
                {
                    if (field.Length == MaxFieldLength)
                        throw new AgwException(
                            ErrorCodes.ShellLoginPathUnavailable,
                            $"Login shell search path exceeds {MaxFieldLengthKiB} KiB."
                        );

                    field.Append(character);
                }
            }
        }
    }
}
