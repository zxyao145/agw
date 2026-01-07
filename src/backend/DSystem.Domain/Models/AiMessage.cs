namespace DSystem.Domain.Models;

public enum AiMessageType
{
    Text,
    Image,
    File,
    // Add other types as needed
}
public record AiMessage(string MessageId, string? Author, string? Role, AiMessageType Type, string Content);


public record AiMessageContent(string Type, string Content);

public record AiMessage2(string MessageId, string? Author, string? Role, List<AiMessageContent> Contents);
