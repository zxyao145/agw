using Agw.Shared.Exceptions;
using PiAgentSdk.Internal;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal sealed record AcpResolvedCommand(string Path, string? LoginShellPath);

internal static class AcpCommandResolver
{
    public static async Task<AcpResolvedCommand> ResolveAsync(
        string command,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken ct
    )
    {
        var result = await CommandUtil.FindAsync(command, new CommandLookupOptions { Environment = environment }, ct);
        if (result.Path is not { } path)
            throw new AgwException(ErrorCodes.AcpAgentStartFailed, $"Command '{command}' was not found.");
        if (OperatingSystem.IsWindows() && Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat")
            throw new AgwException(
                ErrorCodes.AcpAgentStartFailed,
                $"Windows command scripts are not supported: {path}."
            );
        return new(path, result.SearchPath);
    }
}
