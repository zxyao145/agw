using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents;

/// <summary>Marks Result messages from schema-enabled executions without changing their content.</summary>
internal static class ResponseSchemaResultMetadata
{
    internal static void Apply(ChatMessage message) =>
        message.AdditionalProperties = Apply(message.AdditionalProperties, message.Contents);

    internal static void Apply(AgentResponseUpdate update) =>
        update.AdditionalProperties = Apply(update.AdditionalProperties, update.Contents);

    private static AdditionalPropertiesDictionary? Apply(
        AdditionalPropertiesDictionary? properties,
        IEnumerable<AIContent> contents
    )
    {
        if (
            properties?.GetValueOrDefault("type")?.ToString() != "result"
            && !contents.Any(content => content.AdditionalProperties?.GetValueOrDefault("type")?.ToString() == "result")
        )
        {
            return properties;
        }

        var result =
            properties == null ? new AdditionalPropertiesDictionary() : new AdditionalPropertiesDictionary(properties);
        result["resultFormat"] = JsonSerializer.SerializeToElement(ResultFormat.Json).GetString()!;
        return result;
    }
}
