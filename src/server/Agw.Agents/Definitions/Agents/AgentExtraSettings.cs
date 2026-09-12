using System.Text.Json;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Definitions.Agents;

public static class AgentExtraSettings
{
    public static string? Normalize(string? extra)
    {
        var normalized = extra?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return normalized;
            }
        }
        catch (JsonException) { }

        throw new AgwException(ErrorCodes.InvalidAgentExtraSettings);
    }
}
