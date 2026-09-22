using System.Text.Json;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Utils;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Application.History;

public static class ProjectConversationChatHistoryChatMessageExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;

    public static ChatMessage? ToChatMessage(this ProjectConversationChatHistory record)
    {
        if (string.IsNullOrWhiteSpace(record.ConversationPayload))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ChatMessage>(record.ConversationPayload, JsonOptions);
    }

    public static string GetText(this ProjectConversationChatHistory record)
    {
        return string.Concat(
            record.ToChatMessage()?.Contents.OfType<TextContent>().Select(content => content.Text) ?? []
        );
    }
}
