namespace DSystem.Domain.Models;

public enum AiMessageType
{
    Text,
    Image,
    File,
    // Add other types as needed
}
public record AiMessage(string MessageId, string? Author, string? Role, AiMessageType Type, string Content);
