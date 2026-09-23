using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

internal class ModelMessageAdapter : IAgentMessageAdapter<AgentResponseUpdate>
{
    private readonly Dictionary<string, (ChatRole Role, string? Author)> _headers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Type Type, bool Opaque, int Index)> _tails = new(StringComparer.Ordinal);

    public virtual ChatMessage? Map(AgentResponseUpdate update)
    {
        var message = ToMessage(update);
        if (
            ConversationHistoryMetadata.IsPersistenceExcluded(message)
            || ConversationHandoffMetadata.IsHandoffMessage(message)
        )
            return null;
        var id = SourceId(message);
        var headerKey = $"{id}:{AgentMessageProjection.GetPurpose(message)}";
        if (_headers.TryGetValue(headerKey, out var previous))
        {
            message.Role = update.Role ?? previous.Role;
            message.AuthorName ??= previous.Author;
        }
        _headers[headerKey] = (message.Role, message.AuthorName);
        message.MessageId = id;
        message.Contents = [];
        foreach (var content in update.Contents.Where(content => content is not UsageContent))
        {
            var copy = AgentMessageProjection.CloneContent(content);
            copy.AdditionalProperties ??= [];
            copy.AdditionalProperties["blockId"] = GetBlockId(update, content, id, message);
            message.Contents.Add(copy);
        }
        return message.Contents.Count == 0 ? null : message;
    }

    protected virtual string GetBlockId(
        AgentResponseUpdate update,
        AIContent content,
        string sourceId,
        ChatMessage header
    )
    {
        if (content.AdditionalProperties?.GetValueOrDefault("blockId")?.ToString() is { Length: > 0 } explicitId)
            return explicitId;

        var key = $"{sourceId}:{header.Role}:{header.AuthorName}:{AgentMessageProjection.GetPurpose(header)}";
        if (!_tails.TryGetValue(key, out var tail))
            tail = (content.GetType(), content is TextReasoningContent { ProtectedData: not null }, 0);
        else if (
            tail.Type != content.GetType()
            || tail.Opaque != (content is TextReasoningContent { ProtectedData: not null })
            || content is not (TextContent or TextReasoningContent)
        )
            tail = (content.GetType(), content is TextReasoningContent { ProtectedData: not null }, tail.Index + 1);
        _tails[key] = tail;
        return $"block:{tail.Index}";
    }

    private static string SourceId(ChatMessage message)
    {
        var source = message.AdditionalProperties?.GetValueOrDefault("sourceMessageId")?.ToString();
        if (!string.IsNullOrWhiteSpace(source))
            return source;
        if (!string.IsNullOrWhiteSpace(message.MessageId))
            return message.MessageId;
        if (message.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { CallId.Length: > 0 } result)
            return $"tool:{result.CallId}";
        return AgentMessageProjection.GetPurpose(message) == "result" ? "result"
            : message.Role == ChatRole.Assistant ? "response"
            : Guid.CreateVersion7().ToString("D");
    }

    internal static ChatMessage ToMessage(AgentResponseUpdate update) =>
        new(update.Role ?? ChatRole.Assistant, update.Contents)
        {
            MessageId = update.MessageId,
            AuthorName = update.AuthorName,
            CreatedAt = update.CreatedAt,
            AdditionalProperties = update.AdditionalProperties == null ? null : new(update.AdditionalProperties),
        };
}

internal sealed class CodexMessageAdapter : ModelMessageAdapter
{
    public override ChatMessage? Map(AgentResponseUpdate update) =>
        update.AdditionalProperties?.GetValueOrDefault("type")?.ToString() is "turn.started" or "turn.completed"
            ? null
            : base.Map(update);
}

internal sealed class ClaudeMessageAdapter : ModelMessageAdapter
{
    public override ChatMessage? Map(AgentResponseUpdate update) =>
        ClaudeCodeMessagePolicy.IsTransportEvent(update.Role, update.AdditionalProperties, update.Contents)
            ? null
            : base.Map(update);

    protected override string GetBlockId(
        AgentResponseUpdate update,
        AIContent content,
        string sourceId,
        ChatMessage header
    ) =>
        (content.RawRepresentation ?? update.RawRepresentation) is StreamEvent stream
        && stream.Event.TryGetProperty("index", out var index)
        && index.TryGetInt32(out var value)
            ? $"block:{value}"
            : base.GetBlockId(update, content, sourceId, header);
}
