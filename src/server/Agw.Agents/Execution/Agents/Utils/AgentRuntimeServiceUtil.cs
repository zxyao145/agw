using Agw.Files.Utils;

namespace Agw.Agents.Execution.Agents.Utils;

public static class AgentRuntimeServiceUtil
{
    public static IReadOnlyDictionary<string, string> MergeEnvironmentVariables(
        params IReadOnlyDictionary<string, string>?[] variableLayers)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variables in variableLayers)
        {
            if (variables == null)
            {
                continue;
            }

            foreach (var (key, value) in variables)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    public static string BuildInstructions(string? systemPrompt, string? workspace)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            systemPrompt = "You are a helpful agent.";
        }

        if (string.IsNullOrWhiteSpace(workspace))
        {
            return systemPrompt;
        }

        workspace = PathUtil.ExpandTilde(workspace);
        var workspaceInstructions =
            $"""
            # others

            - Your default workspace or working directory is '{workspace}'.
            """;

        return $"{systemPrompt}{Environment.NewLine}{workspaceInstructions}";
    }

}
