using System.Text.Json;

using Microsoft.Extensions.AI;

namespace Agw.Tasks.Domain.Services;

public static class TaskRecordMetadataFactory
{
    public static Dictionary<string, JsonElement>? FromMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var properties = message.Contents
            .OfType<TextContent>()
            .Select(content => content.AdditionalProperties)
            .FirstOrDefault(additionalProperties =>
                additionalProperties != null
                && additionalProperties.ContainsKey("targetType")
                && additionalProperties.ContainsKey("targetId"));

        if (properties == null)
        {
            return null;
        }

        return new Dictionary<string, JsonElement>
        {
            ["targetType"] = JsonSerializer.SerializeToElement(properties["targetType"]),
            ["targetId"] = JsonSerializer.SerializeToElement(properties["targetId"]),
        };
    }
}
