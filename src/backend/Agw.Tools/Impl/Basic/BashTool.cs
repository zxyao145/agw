using System.Diagnostics;

using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Basic;

public class BashToolParams
{
    [Description(
        """
        The command to execute.
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

        For simple commands (git, npm, standard CLI tools), keep it brief (5-10 words):
        - ls → "List files in current directory"
        - git status → "Show working tree status"
        - npm install → "Install package dependencies"

        For commands that are harder to parse at a glance (piped commands, obscure flags, etc.), add enough context to clarify what it does:
        - find . -name "*.tmp" -exec rm {} \; → "Find and delete all .tmp files recursively"
        - git reset --hard origin/main → "Discard all local changes and match remote main"
        - curl -s url | jq '.data[]' → "Fetch JSON from URL and extract data array elements"
        """
    )]
    public string? Description { get; set; }
}

public class BashToolResult
{
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public int ExitCode { get; set; }
    public long DurationMs { get; set; }
}

internal class BashTool : IAgwTool
{
    public string Name => "bash";

    public string Category => "Bash";

    [Description(
        """
        Executes a given bash command and returns its output.
        The working directory persists between commands, but shell state does not.
        The shell environment is initialized from the user's profile (bash or zsh).
        """
    )]
    public BashToolResult Execute(BashToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.Command))
        {
            throw new AgwException(ErrorCodes.CommandRequired, "Command is required.");
        }

        var stopwatch = Stopwatch.StartNew();

        var isWindows = OperatingSystem.IsWindows();
        var shell = isWindows ? "cmd.exe" : "/bin/bash";
        var shellArg = isWindows ? "/c" : "-c";

        var processInfo = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = $"{shellArg} \"{toolParams.Command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.GetCurrentDirectory()
        };

        using var process = new Process();
        process.StartInfo = processInfo;

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new AgwException(ErrorCodes.CommandExecutionFailed, $"Failed to start process: {ex.Message}");
        }

        var timeoutMs = toolParams.Timeout ?? 120000;
        var completed = process.WaitForExit(timeoutMs);

        if (!completed)
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // Ignore kill errors
            }
            throw new AgwException(ErrorCodes.CommandTimeout, $"Command timed out after {timeoutMs}ms.");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        var exitCode = process.ExitCode;

        stopwatch.Stop();

        return new BashToolResult
        {
            Stdout = stdout,
            Stderr = stderr,
            ExitCode = exitCode,
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    public AITool ToAITool()
    {
        Func<BashToolParams, BashToolResult> func = Execute;
        var aiTool = AIFunctionFactory.Create(func, Name);
        return aiTool;
    }
}
