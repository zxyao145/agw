using Microsoft.Extensions.AI;

namespace Agw.Tools.Runtime;

internal static class ContextAttachmentHistory
{
    public static string? GetAttachmentType(ChatMessage message)
    {
        var properties = message.AdditionalProperties;
        return properties?.GetValueOrDefault(ConversationHistoryMetadata.ContextAttachmentTypeKey)?.ToString();
    }

    public static void SetAttachmentType(ChatMessage message, string type)
    {
        message.AdditionalProperties ??= [];
        message.AdditionalProperties[ConversationHistoryMetadata.ContextAttachmentTypeKey] = type;
    }

    public static int CountAssistantMessagesAfterLast(
        IReadOnlyList<ChatMessage> messages,
        Func<ChatMessage, bool> predicate
    )
    {
        var count = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (predicate(message))
                break;
            if (message.Role == ChatRole.Assistant)
                count++;
        }
        return count;
    }
}
