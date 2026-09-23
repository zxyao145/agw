using System.Text;
using System.Text.Json;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

internal interface IAgentMessageAdapter<in TEvent>
{
    ChatMessage? Map(TEvent nativeEvent);
}

/// <summary>
/// <para>按消息和内容块身份累积追加内容，并提供持久化快照。</para>
/// <para>Accumulates appended content by message and block identity and supplies persistence snapshots.</para>
/// </summary>
internal sealed class AgentMessageProjection : IConversationMessageSource
{
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;
    private readonly object _gate = new();
    private readonly ConversationMessageWriteScope _scope;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, MessageEntry> _messages = new(StringComparer.Ordinal);

    internal AgentMessageProjection(ConversationMessageWriteScope scope, TimeProvider timeProvider)
    {
        _scope = scope;
        _timeProvider = timeProvider;
    }

    internal ChatMessage Append(ChatMessage message, bool newMessage = false)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId) || message.Contents.Count == 0)
            throw new AgwException(ErrorCodes.InvalidParam);

        lock (_gate)
        {
            var purpose = GetPurpose(message);
            var identity = JsonSerializer.Serialize(
                new[] { message.MessageId, message.Role.Value, message.AuthorName, purpose }
            );
            if (newMessage)
                identity += Guid.CreateVersion7().ToString("D");
            if (!_messages.TryGetValue(identity, out var entry))
            {
                var header = Clone(message);
                header.Contents.Clear();
                entry = new MessageEntry
                {
                    Id = Guid.CreateVersion7(),
                    SourceId = message.MessageId,
                    Purpose = purpose,
                    Header = header,
                    CreatedAt = message.CreatedAt ?? _timeProvider.GetUtcNow(),
                };
                _messages.Add(identity, entry);
            }
            entry.Header.AdditionalProperties ??= [];
            foreach (var (key, value) in message.AdditionalProperties ?? [])
                entry.Header.AdditionalProperties[key] = value;
            entry.Header.AdditionalProperties["messagePurpose"] = purpose;

            var output = Clone(entry.Header);
            foreach (var value in message.Contents)
            {
                var content = CloneContent(value);
                var blockId = content.AdditionalProperties?.GetValueOrDefault("blockId")?.ToString();
                var text = content switch
                {
                    TextContent textContent => textContent.Text,
                    TextReasoningContent { ProtectedData: null } reasoning => reasoning.Text,
                    _ => null,
                };
                if (text != null && blockId != null && entry.TextBlocks.TryGetValue(blockId, out var block))
                {
                    if (block.Content.GetType() != content.GetType())
                        throw new AgwException(ErrorCodes.ConversationSessionConflict);
                    block.Text!.Append(text);
                    block.Content.AdditionalProperties ??= [];
                    foreach (var (key, item) in content.AdditionalProperties ?? [])
                        block.Content.AdditionalProperties[key] = item;
                    if (content.Annotations?.Count > 0)
                        block.Content.Annotations = [.. block.Content.Annotations ?? [], .. content.Annotations];
                }
                else
                {
                    block = new BlockEntry
                    {
                        Content = CloneContent(content),
                        Text = text == null ? null : new StringBuilder(text),
                    };
                    entry.Blocks.Add(block);
                    if (text != null && blockId != null)
                        entry.TextBlocks.Add(blockId, block);
                }
                output.Contents.Add(content);
            }
            entry.Dirty = true;
            entry.Cached = null;
            Stamp(output, entry);
            return output;
        }
    }

    internal IReadOnlyList<ChatMessage> ReadMessages()
    {
        lock (_gate)
            return _messages
                .Values.Select(entry =>
                    JsonSerializer.Deserialize<ChatMessage>((entry.Cached ??= Snapshot(entry)).Payload, JsonOptions)!
                )
                .ToArray();
    }

    public IReadOnlyList<Guid> GetPendingMessageIds()
    {
        lock (_gate)
            return _messages.Values.Where(entry => entry.Dirty).Select(entry => entry.Id).ToArray();
    }

    public IReadOnlyList<ConversationMessageSnapshot> CapturePending()
    {
        lock (_gate)
            return _messages
                .Values.Where(entry => entry.Dirty)
                .Select(entry => entry.Cached ??= Snapshot(entry))
                .ToArray();
    }

    public void Acknowledge(IReadOnlyList<ConversationMessageSnapshot> snapshots)
    {
        lock (_gate)
        {
            var captured = snapshots.ToDictionary(snapshot => snapshot.MessageId);
            foreach (var entry in _messages.Values)
                if (captured.TryGetValue(entry.Id, out var snapshot) && ReferenceEquals(entry.Cached, snapshot))
                    entry.Dirty = false;
        }
    }

    private ConversationMessageSnapshot Snapshot(MessageEntry entry)
    {
        var message = Clone(entry.Header);
        message.Contents = entry
            .Blocks.Select(block =>
            {
                var content = CloneContent(block.Content);
                if (block.Text != null)
                {
                    if (content is TextContent text)
                        text.Text = block.Text.ToString();
                    else if (content is TextReasoningContent reasoning)
                        reasoning.Text = block.Text.ToString();
                }
                return content;
            })
            .ToList();
        Stamp(message, entry);
        return new ConversationMessageSnapshot
        {
            MessageId = entry.Id,
            CreatedAt = entry.CreatedAt,
            Author = message.AuthorName,
            Payload = JsonSerializer.Serialize(message, JsonOptions),
            Metadata = new()
            {
                ["sourceMessageId"] = JsonSerializer.SerializeToElement(entry.SourceId),
                ["purpose"] = JsonSerializer.SerializeToElement(entry.Purpose),
                ["turnId"] = JsonSerializer.SerializeToElement(_scope.TurnId),
            },
        };
    }

    private void Stamp(ChatMessage message, MessageEntry entry)
    {
        message.MessageId = entry.Id.ToString("D");
        message.CreatedAt = entry.CreatedAt;
        message.AdditionalProperties ??= [];
        message.AdditionalProperties[MessageTimestampMetadata.CreatedAtKey] = entry.CreatedAt;
        message.AdditionalProperties["sourceMessageId"] = entry.SourceId;
        message.AdditionalProperties["conversationGeneration"] = _scope.Generation;
        if (_scope.TurnId != null)
            message.AdditionalProperties["turnId"] = _scope.TurnId.Value.ToString("D");
        message.AdditionalProperties["producerScopeId"] = _scope.ProducerId.ToString("D");
        if (_scope.NodeName != null)
            message.AdditionalProperties["nodeName"] = _scope.NodeName;
        if (message.AdditionalProperties.GetValueOrDefault("resultSourceMessageId")?.ToString() is { } sourceId)
        {
            var source = _messages.Values.FirstOrDefault(value =>
                value.SourceId == sourceId && value.Purpose != "result"
            );
            if (source != null)
                message.AdditionalProperties["resultSourceMessageId"] = source.Id.ToString("D");
        }
    }

    internal static string GetPurpose(ChatMessage message) =>
        message.AdditionalProperties?.GetValueOrDefault("messagePurpose")?.ToString() == "result"
        || message.AdditionalProperties?.GetValueOrDefault("type")?.ToString() == "result"
        || message.Contents.Any(content =>
            content.AdditionalProperties?.GetValueOrDefault("type")?.ToString() == "result"
        )
            ? "result"
            : "message";

    internal static ChatMessage Clone(ChatMessage value) =>
        JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), JsonOptions)!;

    internal static AIContent CloneContent(AIContent value) =>
        JsonSerializer.Deserialize<AIContent>(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), JsonOptions)!;

    private sealed class MessageEntry
    {
        public required Guid Id { get; init; }
        public required string SourceId { get; init; }
        public required string Purpose { get; init; }
        public required ChatMessage Header { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public List<BlockEntry> Blocks { get; } = [];
        public Dictionary<string, BlockEntry> TextBlocks { get; } = new(StringComparer.Ordinal);
        public bool Dirty { get; set; }
        public ConversationMessageSnapshot? Cached { get; set; }
    }

    private sealed class BlockEntry
    {
        public required AIContent Content { get; init; }
        public StringBuilder? Text { get; init; }
    }
}
