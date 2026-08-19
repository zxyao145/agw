using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Agw.Shared.AgwMsgVm;

/// <summary>
/// AI message with role, author, and content blocks.
/// </summary>
public record AgwMessage
{
    public string MessageId { get; init; }
    public string? Author { get; init; }
    public AiRole Role { get; init; } = AiRole.User;
    public List<AgwContent> Contents { get; init; }
    public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }

    [JsonConstructor]
    public AgwMessage(
        string messageId,
        string? author,
        AiRole role,
        List<AgwContent> contents,
        AdditionalPropertiesDictionary? additionalProperties = null
    )
    {
        MessageId = messageId;
        Author = author;
        Role = role;
        Contents = contents;
        AdditionalProperties = additionalProperties;
    }

    public AgwMessage(
        string messageId,
        string? author,
        AiRole role,
        List<AgwContent> contents,
        Dictionary<string, object?>? additionalProperties
    )
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
