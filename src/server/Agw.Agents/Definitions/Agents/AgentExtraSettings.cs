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

    public static string? OmitEmptyObject(string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(extra);
            var root = document.RootElement;
            var isEmpty = root.ValueKind == JsonValueKind.Object && !root.EnumerateObject().Any();
            return isEmpty ? null : extra;
        }
        catch (JsonException)
        {
            return extra;
        }
    }
}
