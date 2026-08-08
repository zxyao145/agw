using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

using Agw.Shared.Exceptions;

using Microsoft.Agents.AI;

namespace Agw.Skills.Execution;

public static class LocalSkillScriptRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public static IReadOnlyList<string> SupportedScriptExtensions { get; } =
        [".py", ".js", ".cs"];

    public static Task<object?> RunAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        return RunAsync(skill.Path, script.FullPath, arguments, cancellationToken);
    }

    internal static async Task<object?> RunAsync(
        string skillPath,
        string scriptPath,
        JsonElement? arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var skillRoot = Path.GetFullPath(skillPath);
        var fullScriptPath = Path.GetFullPath(scriptPath);
        ValidateScriptPath(skillRoot, fullScriptPath);
        var scriptArguments = ParseArguments(arguments);
        var startInfo = CreateStartInfo(skillRoot, fullScriptPath, scriptArguments);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new AgwException(
                    ErrorCodes.CommandExecutionFailed,
                    $"Failed to start skill script '{Path.GetFileName(fullScriptPath)}'.");
            }
        }
        catch (AgwException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AgwException(
                ErrorCodes.CommandExecutionFailed,
                $"Failed to start skill script '{Path.GetFileName(fullScriptPath)}'.",
                exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw new AgwException(
                ErrorCodes.CommandTimeout,
                $"Skill script '{Path.GetFileName(fullScriptPath)}' timed out.");
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(standardError)
                ? $"Skill script '{Path.GetFileName(fullScriptPath)}' exited with code {process.ExitCode}."
                : $"Skill script '{Path.GetFileName(fullScriptPath)}' exited with code {process.ExitCode}: {standardError.Trim()}";
            throw new AgwException(ErrorCodes.CommandExecutionFailed, message);
        }

        return standardOutput.TrimEnd();
    }

    internal static ProcessStartInfo CreateStartInfo(
        string skillRoot,
        string scriptPath,
        IReadOnlyList<string> scriptArguments)
    {
        var extension = Path.GetExtension(scriptPath);
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = skillRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = OperatingSystem.IsWindows() ? "python" : "python3";
            startInfo.ArgumentList.Add(scriptPath);
        }
        else if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "node";
            startInfo.ArgumentList.Add(scriptPath);
        }
        else if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "dotnet";
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--file");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("--");
        }
        else
        {
            throw new AgwException(
                ErrorCodes.CommandExecutionFailed,
                $"Skill script extension '{extension}' is not supported.");
        }

        foreach (var argument in scriptArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void ValidateScriptPath(string skillRoot, string scriptPath)
    {
        if (!Directory.Exists(skillRoot))
        {
            throw new AgwException(
                ErrorCodes.CommandExecutionFailed,
                $"Skill directory '{skillRoot}' does not exist.");
        }

        var relativePath = Path.GetRelativePath(skillRoot, scriptPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new AgwException(
                ErrorCodes.CommandExecutionFailed,
                $"Skill script '{scriptPath}' is outside the skill directory.");
        }

        if (!File.Exists(scriptPath))
        {
            throw new AgwException(
                ErrorCodes.CommandExecutionFailed,
                $"Skill script '{scriptPath}' does not exist.");
        }

        var extension = Path.GetExtension(scriptPath);
        if (!SupportedScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new AgwException(
                ErrorCodes.CommandExecutionFailed,
                $"Skill script extension '{extension}' is not supported.");
        }
    }

    private static IReadOnlyList<string> ParseArguments(JsonElement? arguments)
    {
        if (!arguments.HasValue)
        {
            return [];
        }

        if (arguments.Value.ValueKind != JsonValueKind.Array)
        {
            throw new AgwException(
                ErrorCodes.CommandExecutionFailed,
                "Skill script arguments must be an array of strings.");
        }

        var values = new List<string>();
        foreach (var argument in arguments.Value.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String)
            {
                throw new AgwException(
                    ErrorCodes.CommandExecutionFailed,
                    "Skill script arguments must be an array of strings.");
            }

            values.Add(argument.GetString()!);
        }

        return values;
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
