using DSystem.Domain.Entities;
using DSystem.Shared;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace DSystem.Domain.Services;

public static class TaskRecordChatMessageExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ChatMessage? ToChatMessage(this TaskRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ConversationPayload))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ChatMessage>(record.ConversationPayload, JsonOptions);
    }

    public static string GetText(this TaskRecord record)
    {
        return string.Concat(
            record.ToChatMessage()?.Contents
                .OfType<TextContent>()
                .Select(content => content.Text)
            ?? []);
    }
}
