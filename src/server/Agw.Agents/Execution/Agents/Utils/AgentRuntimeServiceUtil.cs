using Agw.Shared.Utils;

namespace Agw.Agents.Execution.Agents.Utils;

public static class AgentRuntimeServiceUtil
{
    public static IReadOnlyDictionary<string, string> MergeEnvironmentVariables(
        IReadOnlyDictionary<string, string>? agentVariables,
        IReadOnlyDictionary<string, string>? executionVariables)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (agentVariables != null)
        {
            foreach (var (key, value) in agentVariables)
            {
                merged[key] = value;
            }
        }

        if (executionVariables != null)
        {
            foreach (var (key, value) in executionVariables)
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
