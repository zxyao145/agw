using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Agw.Shared.Exceptions;
using Agw.Tools.Contracts.Abstractions;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Basic;

public class PowerShellToolParams
{
    [Description(
        """
            The PowerShell command to execute.
            """
    )]
    public string Command { get; set; } = "";

    [Description(
        """
            Optional timeout in milliseconds (max 600000ms / 10 minutes). By default, your command will timeout after 120000ms (2 minutes).
            """
    )]
    public int? Timeout { get; set; }

    [Description(
        """
            Clear, concise description of what this command does in active voice. Never use words like "complex" or "risk" in the description - just describe what it does.

            For simple commands (Get-ChildItem, npm, git, standard cmdlets), keep it brief (5-10 words):
            - Get-ChildItem → "List items in current directory"
            - git status → "Show working tree status"
            - npm install → "Install package dependencies"

            For commands that are harder to parse at a glance (piped commands, obscure flags, etc.), add enough context to clarify what it does:
            - Get-ChildItem -Recurse -Filter *.tmp | Remove-Item -Force → "Find and delete all .tmp files recursively"
            - git reset --hard origin/main → "Discard all local changes and match remote main"
            - Invoke-RestMethod url | Select-Object -ExpandProperty data → "Fetch JSON from URL and extract data array elements"
            """
    )]
    public string? Description { get; set; }
}

public class PowerShellToolResult
{
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public int ExitCode { get; set; }
    public long DurationMs { get; set; }
    public string Shell { get; set; } = "";

    /// <summary>
    /// Non-fatal warnings about potentially destructive command patterns.
    /// Inspired by PowerShellTool/destructiveCommandWarning.ts in claude-code.
    /// </summary>
    public List<string> Warnings { get; set; } = [];
}

[Obsolete("Use BuiltIn ShellTool instead")]
internal class PowerShellTool : IAgwTool
{
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxTimeoutMs = 600_000;

    /// <summary>
    /// Patterns that flag potentially destructive operations.
    /// Purely informational – does not block execution.
    /// Mirrors PowerShellTool/destructiveCommandWarning.ts in claude-code.
    /// </summary>
    private static readonly (Regex Pattern, string Warning)[] DestructivePatterns =
    [
        (
            new Regex(
                @"(?:^|[|;&\n({])\s*(Remove-Item|rm|del|rd|rmdir|ri)\b[^|;&\n}]*-Recurse\b[^|;&\n}]*-Force\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled
            ),
            "Note: may recursively force-remove files"
        ),
        (
            new Regex(
                @"(?:^|[|;&\n({])\s*(Remove-Item|rm|del|rd|rmdir|ri)\b[^|;&\n}]*-Force\b[^|;&\n}]*-Recurse\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled
            ),
            "Note: may recursively force-remove files"
        ),
        (
            new Regex(
                @"(?:^|[|;&\n({])\s*(Remove-Item|rm|del|rd|rmdir|ri)\b[^|;&\n}]*-Recurse\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled
            ),
            "Note: may recursively remove files"
        ),
        (
            new Regex(
                @"(?:^|[|;&\n({])\s*(Remove-Item|rm|del|rd|rmdir|ri)\b[^|;&\n}]*-Force\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled
            ),
            "Note: may force-remove files"
        ),
        (
            new Regex(@"\bClear-Content\b[^|;&\n]*\*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Note: may clear content of multiple files"
        ),
        (
            new Regex(@"\bFormat-Volume\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Note: may format a disk volume"
        ),
        (new Regex(@"\bClear-Disk\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Note: may clear a disk"),
        (
            new Regex(@"\bgit\s+reset\s+--hard\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Note: discards local changes (git reset --hard)"
        ),
        (
            new Regex(@"\bgit\s+push\s+(--force|-f)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Note: force-pushes to remote (git push --force)"
        ),
        (
            new Regex(@"\bgit\s+clean\s+-[a-z]*f[a-z]*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Note: removes untracked files (git clean -f)"
        ),
        (
            new Regex(@"\bStop-Computer\b|\bRestart-Computer\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Note: shuts down or restarts the computer"
        ),
    ];

    /// <summary>
    /// Commands that are interactive / blocking and would hang under -NonInteractive.
    /// Mirrors the "Interactive and blocking commands" section of prompt.ts.
    /// </summary>
    private static readonly Regex InteractivePattern = new(
        @"\b(Read-Host|Get-Credential|Out-GridView|pause)\b|\bgit\s+(rebase|add|commit)\s+-i\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public string Name => "powershell";

    public string Category => "Bash";

    [Description(
        """
            Executes a given PowerShell command with optional timeout and returns its output.

            IMPORTANT: This tool is for terminal operations via PowerShell: git, npm, docker, and PS cmdlets. DO NOT use it for file operations (reading, writing, editing, searching, finding files) – use the specialized tools for that instead.

            The working directory persists between commands; shell state (variables, functions) does not.
            The shell is invoked with -NoProfile -NonInteractive -ExecutionPolicy Bypass.

            PowerShell Syntax Notes:
               - Variables use $ prefix: $myVar = "value"
               - Escape character is backtick (`), not backslash
               - Use Verb-Noun cmdlet naming: Get-ChildItem, Set-Location, New-Item, Remove-Item
               - Common aliases: ls (Get-ChildItem), cd (Set-Location), cat (Get-Content), rm (Remove-Item)
               - Pipe operator | passes objects, not text
               - String interpolation: "Hello $name" or "Hello $($obj.Property)"
               - Environment variables: read with $env:NAME, set with $env:NAME = "value"

            Interactive and blocking commands (will hang under -NonInteractive):
               - NEVER use Read-Host, Get-Credential, Out-GridView, $Host.UI.PromptForChoice, or pause
               - Destructive cmdlets (Remove-Item, Stop-Process, Clear-Content, etc.) may prompt for confirmation. Add -Confirm:$false when you intend the action to proceed. Use -Force for read-only/hidden items.
               - Never use git rebase -i, git add -i, or other commands that open an interactive editor

            Usage notes:
              - The Command argument is required.
              - You can specify an optional Timeout in milliseconds (up to 600000ms / 10 minutes). Default: 120000ms (2 minutes).
              - For git commands: prefer creating a new commit over amending; avoid --no-verify / --no-gpg-sign unless explicitly asked.
            """
    )]
    public PowerShellToolResult Execute(PowerShellToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.Command))
        {
            throw new AgwException(ErrorCodes.CommandRequired, "Command is required.");
        }

        var timeoutMs = NormalizeTimeout(toolParams.Timeout);
        var warnings = CollectWarnings(toolParams.Command);

        var stopwatch = Stopwatch.StartNew();

        var (shell, isCore) = ResolvePowerShellExecutable();
        var arguments = BuildShellArguments(toolParams.Command, isCore);

        var processInfo = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process();
        process.StartInfo = processInfo;

        // Drain stdout / stderr asynchronously so a large output stream
        // cannot deadlock the child process by filling the OS pipe buffer.
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdoutBuilder.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderrBuilder.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new AgwException(
                ErrorCodes.CommandExecutionFailed,
                $"Failed to start PowerShell ('{shell}'): {ex.Message}"
            );
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var completed = process.WaitForExit(timeoutMs);

        if (!completed)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort – the process may have exited between checks.
            }

            throw new AgwException(ErrorCodes.CommandTimeout, $"PowerShell command timed out after {timeoutMs}ms.");
        }

        // WaitForExit(int) does not flush the async readers; the parameterless
        // overload does. Call it after the bounded wait succeeded.
        process.WaitForExit();

        stopwatch.Stop();

        return new PowerShellToolResult
        {
            Stdout = stdoutBuilder.ToString(),
            Stderr = stderrBuilder.ToString(),
            ExitCode = process.ExitCode,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Shell = shell,
            Warnings = warnings,
        };
    }

    public AITool ToAITool()
    {
        Func<PowerShellToolParams, PowerShellToolResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }

    private static int NormalizeTimeout(int? requested)
    {
        if (requested is null || requested <= 0)
        {
            return DefaultTimeoutMs;
        }

        return Math.Min(requested.Value, MaxTimeoutMs);
    }

    private static List<string> CollectWarnings(string command)
    {
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (pattern, warning) in DestructivePatterns)
        {
            if (pattern.IsMatch(command) && seen.Add(warning))
            {
                warnings.Add(warning);
            }
        }

        if (InteractivePattern.IsMatch(command))
        {
            const string interactive = "Note: command appears interactive/blocking and may hang under -NonInteractive";
            if (seen.Add(interactive))
            {
                warnings.Add(interactive);
            }
        }

        return warnings;
    }

    /// <summary>
    /// Returns the PowerShell executable to invoke and whether it is the
    /// cross-platform PowerShell 7+ (pwsh) edition. Prefers pwsh; falls back
    /// to Windows PowerShell 5.1 (powershell.exe) when pwsh is unavailable.
    /// </summary>
    private static (string Shell, bool IsCore) ResolvePowerShellExecutable()
    {
        if (TryLocate("pwsh", out var pwshPath))
        {
            return (pwshPath, true);
        }

        if (OperatingSystem.IsWindows() && TryLocate("powershell", out var legacyPath))
        {
            return (legacyPath, false);
        }

        // Fall back to bare names; Process.Start will surface a clear error
        // if neither is on PATH at execution time.
        return OperatingSystem.IsWindows() ? ("powershell.exe", false) : ("pwsh", true);
    }

    private static bool TryLocate(string command, out string resolvedPath)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            resolvedPath = command;
            return false;
        }

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };

        foreach (var dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), command + ext);
                if (File.Exists(candidate))
                {
                    resolvedPath = candidate;
                    return true;
                }
            }
        }

        resolvedPath = command;
        return false;
    }

    /// <summary>
    /// Builds the argument string for the PowerShell host:
    ///   -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "command"
    /// The user-provided command is wrapped to ensure quoting survives the
    /// Win32 CommandLineToArgvW round-trip used by ProcessStartInfo.Arguments.
    /// </summary>
    private static string BuildShellArguments(string command, bool isCore)
    {
        // Both editions accept these flags; -ExecutionPolicy is a no-op on
        // non-Windows pwsh but harmless.
        var baseFlags = "-NoProfile -NonInteractive -ExecutionPolicy Bypass";

        // Escape embedded double-quotes for the outer wrapping.
        // PowerShell -Command accepts a single argument; we wrap the whole
        // command in double quotes and double-up internal quotes per the
        // Win32 quoting rules used by Process.Start when not using
        // ArgumentList.
        var escaped = command.Replace("\"", "\\\"");

        // -Command is supported on both Windows PowerShell 5.1 and pwsh 7+.
        // We intentionally avoid -File (requires a script path) and
        // -EncodedCommand (obscures intent / triggers downstream security
        // scanners).
        _ = isCore; // Reserved for edition-specific tweaks in the future.
        return $"{baseFlags} -Command \"{escaped}\"";
    }
}
