namespace PiAgentSdk.Internal;

internal static class PiProcessArguments
{
    public static IReadOnlyList<string> Build(PiSessionOptions options, string? resumeSessionId)
    {
        var arguments = new List<string> { "--mode", "rpc" };
        Add(arguments, "--provider", options.Provider);
        Add(arguments, "--model", options.Model);
        Add(arguments, "--thinking", options.ThinkingLevel);
        Add(arguments, "--session-dir", options.SessionDir);
        Add(arguments, "--name", options.SessionName);

        if (options.NoSession)
        {
            arguments.Add("--no-session");
        }
        else
        {
            Add(arguments, "--session", resumeSessionId);
        }

        if (options.Tools is { Count: > 0 })
        {
            Add(arguments, "--tools", string.Join(',', options.Tools));
        }

        if (options.ExcludeTools is { Count: > 0 })
        {
            Add(arguments, "--exclude-tools", string.Join(',', options.ExcludeTools));
        }

        if (options.NoExtensions)
        {
            arguments.Add("--no-extensions");
        }

        if (options.Extensions != null)
        {
            foreach (var extension in options.Extensions)
            {
                Add(arguments, "--extension", extension);
            }
        }

        arguments.Add(options.ProjectTrust == PiProjectTrust.Approve ? "--approve" : "--no-approve");
        return arguments;
    }

    private static void Add(ICollection<string> arguments, string flag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        arguments.Add(flag);
        arguments.Add(value);
    }
}
