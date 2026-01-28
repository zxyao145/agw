using Microsoft.Extensions.AI;
using System.Text.Json;

namespace DSystem.Domain.Models;

/// <summary>
/// Content type names matching Microsoft.Extensions.AI content types.
/// </summary>
public static class AiMessageContentType
{
    public const string DataContent = nameof(DataContent);
    public const string ErrorContent = nameof(ErrorContent);
    public const string FunctionCallContent = nameof(FunctionCallContent);
    public const string FunctionResultContent = nameof(FunctionResultContent);
    public const string HostedFileContent = nameof(HostedFileContent);
    public const string HostedVectorStoreContent = nameof(HostedVectorStoreContent);
    public const string TextContent = nameof(TextContent);
    public const string TextReasoningContent = nameof(TextReasoningContent);
    public const string UriContent = nameof(UriContent);
    public const string UsageContent = nameof(UsageContent);
}

/// <summary>
/// Input content for AI messages with typed content data.
/// </summary>
public record AiMessageInputContent(string Type, JsonElement Content);

/// <summary>
/// Content within an AI message.
/// </summary>
public record AiMessageContent(
    string Type,
    object? Content,
    AdditionalPropertiesDictionary? AdditionalProperties = null
);

/// <summary>
/// AI message with role, author, and content blocks.
/// </summary>
public record AiMessage(
    string MessageId,
    string? Author,
    string? Role,
    List<AiMessageContent> Contents,
    AdditionalPropertiesDictionary? AdditionalProperties = null
);
