using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Contracts.Messages;

/// <summary>Preserves display timestamps in SDK messages and their persisted JSON.</summary>
public static class MessageTimestampMetadata
{
    public const string CreatedAtKey = "createdAt";

    public static DateTimeOffset? GetCreatedAt(AdditionalPropertiesDictionary? properties)
    {
        if (properties?.TryGetValue(CreatedAtKey, out var value) != true)
            return null;
        if (value is DateTimeOffset timestamp)
            return timestamp;
        var text = value is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : value as string;
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out timestamp
        )
            ? timestamp
            : null;
    }

    public static AdditionalPropertiesDictionary WithCreatedAt(
        AdditionalPropertiesDictionary? properties,
        DateTimeOffset createdAt
    )
    {
        var result =
            properties == null ? new AdditionalPropertiesDictionary() : new AdditionalPropertiesDictionary(properties);
        result[CreatedAtKey] = createdAt;
        return result;
    }

    public static ChatMessage EnsureCreatedAt(ChatMessage message, DateTimeOffset fallback)
    {
        var timestamp = GetCreatedAt(message.AdditionalProperties) ?? message.CreatedAt ?? fallback;
        message.CreatedAt = timestamp;
        message.AdditionalProperties = WithCreatedAt(message.AdditionalProperties, timestamp);
        return message;
    }
}
