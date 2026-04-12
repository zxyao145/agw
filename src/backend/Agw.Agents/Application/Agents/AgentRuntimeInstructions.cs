using Agw.Shared.Utils;

namespace Agw.Appliaction.Services.Agents;

public static class AgentRuntimeInstructions
{
    public static string BuildInstructions(string? systemPrompt, string? workspace)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            systemPrompt = "You are an AI agent.";
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
