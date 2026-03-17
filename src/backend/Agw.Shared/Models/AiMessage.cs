using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agw.Shared.Models;


/// <summary>
/// AI message with role, author, and content blocks.
/// </summary>
public record AiMessage
{
    public string MessageId { get; init; }
    public string? Author { get; init; }
    public AiRole Role { get; init; } = AiRole.User;
    public List<AiMessageContent> Contents { get; init; }
    public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }

    [JsonConstructor]
    public AiMessage(
        string messageId,
        string? author,
        AiRole role,
        List<AiMessageContent> contents,
        AdditionalPropertiesDictionary? additionalProperties = null)
    {
        MessageId = messageId;
        Author = author;
        Role = role;
        Contents = contents;
        AdditionalProperties = additionalProperties;        
    }

    public AiMessage(
        string messageId,
        string? author,
        AiRole role,
        List<AiMessageContent> contents,
        Dictionary<string, object?>? additionalProperties)
    {
        MessageId = messageId;
        Author = author;
        Role = role;
        Contents = contents;
        if (additionalProperties != null)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary(additionalProperties);
        }
    }
}
