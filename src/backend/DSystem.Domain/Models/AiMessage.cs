using Microsoft.Extensions.AI;

namespace DSystem.Domain.Models;

public record AiMessageContent(string Type, object? Content, AdditionalPropertiesDictionary? AdditionalProperties = null);

public record AiMessage(
    string MessageId,
    string? Author,
    string? Role,
    List<AiMessageContent> Contents,
    AdditionalPropertiesDictionary? AdditionalProperties = null
    );
