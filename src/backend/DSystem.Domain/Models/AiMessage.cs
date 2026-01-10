using Microsoft.Extensions.AI;

namespace DSystem.Domain.Models;

public enum AiMessageType
{
    Text,
    Image,
    File,
    // Add other types as needed
}
public record AiMessage(string MessageId, string? Author, string? Role, AiMessageType Type, string Content);


public record AiMessageContent(string Type, object? Content, AdditionalPropertiesDictionary? AdditionalProperties = null);

public record AiMessage2(
    string MessageId,
    string? Author, 
    string? Role, 
    List<AiMessageContent> Contents,
    AdditionalPropertiesDictionary? AdditionalProperties = null
    );
