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

    public static string BuildInstructions(string? systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            systemPrompt = "You are a helpful agent.";
        }

        return systemPrompt;
    }

}
