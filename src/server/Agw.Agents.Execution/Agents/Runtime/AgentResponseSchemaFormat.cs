using System.Text.Json;
using Agw.Providers.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Runtime;

/// <summary>
/// Builds the <see cref="ChatResponseFormat"/> for System agents with a configured response schema.
/// The schema is parsed once per runtime creation; invalid persisted schemas fail instead of degrading
/// to plain text.
/// </summary>
internal static class AgentResponseSchemaFormat
{
    public static ChatResponseFormat? Create(Agent agent, ProviderType providerType)
    {
        if (string.IsNullOrWhiteSpace(agent.ResponseSchema))
        {
            return null;
        }

        JsonElement schema;
        try
        {
            using var document = JsonDocument.Parse(agent.ResponseSchema);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Agent responseSchema must be a JSON object.");
            }

            schema = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Agent responseSchema is not valid JSON.");
        }

        // Anthropic's MEAI adapter silently omits OutputConfig when these fields are absent.
        // Reject that case before execution instead of treating a plain-text turn as structured output.
        if (
            providerType == ProviderType.Anthropic
            && (
                !schema.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "object"
                || !schema.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object
                || !schema.TryGetProperty("required", out var required)
                || required.ValueKind != JsonValueKind.Array
            )
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Anthropic responseSchema must specify type 'object', properties as an object, and required as an array (use [] when all fields are optional)."
            );
        }

        return ChatResponseFormat.ForJsonSchema(schema, SchemaName(agent.Name));
    }

    private static string SchemaName(string name)
    {
        Span<char> buffer = stackalloc char[Math.Min(name.Length, 64)];
        var length = 0;
        foreach (var character in name)
        {
            if (length == buffer.Length)
            {
                break;
            }

            buffer[length++] = char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_';
        }

        return length == 0 || !char.IsAsciiLetterOrDigit(buffer[0]) ? "response_schema" : new string(buffer[..length]);
    }
}
