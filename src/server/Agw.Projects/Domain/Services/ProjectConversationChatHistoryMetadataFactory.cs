using System.Text.Json;

using Agw.Shared.Contracts.Projects;

using Microsoft.Extensions.AI;

namespace Agw.Projects.Domain.Services;

public static class ProjectConversationChatHistoryMetadataFactory
{
    public static Dictionary<string, JsonElement>? FromMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var properties = message.Contents
            .Select(content => content.AdditionalProperties)
            .FirstOrDefault(additionalProperties =>
                additionalProperties != null
                && additionalProperties.ContainsKey("targetType")
                && additionalProperties.ContainsKey("targetId"));

        Dictionary<string, JsonElement>? metadata = null;
        if (properties != null)
        {
            metadata = new Dictionary<string, JsonElement>
            {
                ["targetType"] = JsonSerializer.SerializeToElement(properties["targetType"]),
                ["targetId"] = JsonSerializer.SerializeToElement(properties["targetId"]),
            };
        }

        if (message.AdditionalProperties?.TryGetValue(
                ConversationHandoffMetadata.ThroughSequenceKey,
                out var throughSequence) == true)
        {
            metadata ??= [];
            metadata[ConversationHandoffMetadata.ThroughSequenceKey] =
                JsonSerializer.SerializeToElement(throughSequence);
        }

        return metadata;
    }
}
