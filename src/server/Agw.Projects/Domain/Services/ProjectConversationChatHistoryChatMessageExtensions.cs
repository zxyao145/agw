using System.Text.Json;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Domain.Services;

public static class ProjectConversationChatHistoryChatMessageExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
