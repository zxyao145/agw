using System.Text.Json;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Definitions.Agents;

/// <summary>
/// Normalizes and validates the user-configured agent response schema. The schema is stored and
/// forwarded to runtime adapters as raw JSON text; it is never executed and remote <c>$ref</c>
/// resources are never resolved.
/// </summary>
public static class AgentResponseSchema
{
    /// <summary>
    /// Trims the raw text and requires valid JSON whose root node is an object.
    /// Blank input normalizes to <see langword="null"/>, which disables structured output.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "responseSchema must be a JSON object.");
            }
        }
        catch (JsonException)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "responseSchema must be valid JSON.");
        }

        return trimmed;
    }
}
