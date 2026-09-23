using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

internal enum AgentMessageOperationKind
{
    AppendText,
    PutBlock,
    PutMessage,
    SealMessage,
}

internal sealed record AgentMessageOperation
{
    public Guid? CanonicalId { get; init; }
    public required string SourceId { get; init; }
    public required ChatMessage Header { get; init; }
    public required AgentMessageOperationKind Kind { get; init; }
    public string? BlockId { get; init; }
    public AIContent? Content { get; init; }
    public ConversationMessageState State { get; init; } = ConversationMessageState.Completed;
    public string? EventId { get; init; }
}

internal interface IAgentMessageAdapter<in TEvent>
{
    IReadOnlyList<AgentMessageOperation> Map(TEvent nativeEvent);
    IReadOnlyList<AgentMessageOperation> MapSnapshot(ChatMessage message);
    IReadOnlyList<AgentMessageOperation> MapFinalization();
}

/// <summary>One producer's protocol state. Transport batches and persistence acknowledgements never reset it.</summary>
internal sealed class AgentMessageProjection : IConversationMessageSource
{
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;
    private static readonly Meter Meter = new("Agw.ConversationHistory");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "agw.history.normalize.duration",
        "ms"
    );
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("agw.history.operations");
    private readonly object _gate = new();
    private readonly ConversationMessageWriteScope _scope;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, MessageEntry> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sequences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _events = new(StringComparer.Ordinal);

    internal AgentMessageProjection(ConversationMessageWriteScope scope, TimeProvider timeProvider)
    {
        _scope = scope;
        _timeProvider = timeProvider;
    }

    internal ChatMessage? Apply(AgentMessageOperation operation) => Apply(operation, target: null);

    /// <summary>
    /// <para>按操作定位或新建逻辑消息；<paramref name="target"/> 表示已知条目，沿用其原始身份。</para>
    /// <para>A known <paramref name="target"/> keeps its original identity instead of deriving one
    /// from a header that later operations may have merged into.</para>
    /// </summary>
    private ChatMessage? Apply(AgentMessageOperation operation, MessageEntry? target)
    {
        lock (_gate)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                if (
                    string.IsNullOrWhiteSpace(operation.SourceId)
                    || !Enum.IsDefined(operation.Kind)
                    || operation.Kind is AgentMessageOperationKind.AppendText or AgentMessageOperationKind.PutBlock
                        && (string.IsNullOrWhiteSpace(operation.BlockId) || operation.Content == null)
                    || operation.Kind == AgentMessageOperationKind.AppendText
                        && operation.Content is not (TextContent or TextReasoningContent { ProtectedData: null })
                    || operation.Kind == AgentMessageOperationKind.SealMessage
                        && (operation.State == ConversationMessageState.Open || !Enum.IsDefined(operation.State))
                )
                    throw new AgwException(ErrorCodes.InvalidParam);
                string? eventSignature = null;
                if (operation.EventId is { } eventId)
                {
                    var signature = Convert.ToHexString(
                        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(operation, JsonOptions))
                    );
                    if (_events.TryGetValue(eventId, out var previous))
                    {
                        if (previous != signature)
                            throw new AgwException(ErrorCodes.ConversationSessionConflict);
                        Operations.Add(1, new KeyValuePair<string, object?>("outcome", "duplicate"));
                        return null;
                    }
                    eventSignature = signature;
                }
                var purpose = target?.Purpose ?? GetPurpose(operation.Header);
                var identity =
                    target?.Identity
                    ?? JsonSerializer.Serialize(
                        new[] { operation.SourceId, operation.Header.Role.Value, operation.Header.AuthorName, purpose }
                    );
                var sequence = target?.Index ?? _sequences.GetValueOrDefault(identity);
                var entry = ResolveEntry(identity, sequence, purpose, operation, operation.CanonicalId);
                if (entry.State != ConversationMessageState.Open)
                {
                    if (operation.Kind == AgentMessageOperationKind.PutMessage && SameContents(entry, operation.Header))
                        return null;
                    // An identical final callback may follow a streamed completion.
                    if (
                        operation.Kind == AgentMessageOperationKind.SealMessage
                        && entry.State == operation.State
                        && operation.Header.Contents.Count == 0
                    )
                        return null;
                    // 已封存的消息保留原正文；同一身份的后续内容属于下一条逻辑消息。
                    // A sealed message keeps its content. Anything further under the same identity
                    // opens the next logical message, so the content reaches history in full.
                    entry = ResolveEntry(identity, sequence + 1, purpose, operation, canonicalId: null);
                }
                MergeHeader(entry.Header, operation.Header);
                // The merged header must keep announcing the purpose this entry is keyed by.
                entry.Header.AdditionalProperties!["messagePurpose"] = entry.Purpose;
                var output = Clone(entry.Header);
                output.Contents.Clear();
                switch (operation.Kind)
                {
                    case AgentMessageOperationKind.AppendText:
                    case AgentMessageOperationKind.PutBlock:
                        if (operation.BlockId is not { Length: > 0 } blockId || operation.Content == null)
                            throw new AgwException(ErrorCodes.InvalidParam);
                        var content = CloneContent(operation.Content);
                        content.AdditionalProperties ??= [];
                        content.AdditionalProperties["blockId"] = blockId;
                        var isNewBlock = !entry.Blocks.TryGetValue(blockId, out var block);
                        if (isNewBlock)
                        {
                            block = new BlockEntry { Content = content };
                            entry.Blocks.Add(blockId, block);
                        }
                        if (operation.Kind == AgentMessageOperationKind.AppendText)
                        {
                            var text = content switch
                            {
                                TextContent value => value.Text,
                                TextReasoningContent value => value.Text,
                                _ => throw new AgwException(ErrorCodes.InvalidParam),
                            };
                            if (block!.Content.GetType() != content.GetType())
                                throw new AgwException(ErrorCodes.ConversationSessionConflict);
                            block!.Text ??= new StringBuilder(
                                isNewBlock
                                    ? ""
                                    : block.Content switch
                                    {
                                        TextContent existing => existing.Text,
                                        TextReasoningContent existing => existing.Text,
                                        _ => "",
                                    }
                            );
                            block.Text.Append(text);
                            var properties =
                                block.Content.AdditionalProperties == null
                                    ? new AdditionalPropertiesDictionary()
                                    : new AdditionalPropertiesDictionary(block.Content.AdditionalProperties);
                            foreach (var (name, value) in content.AdditionalProperties)
                                properties[name] = value;
                            content.AdditionalProperties = properties;
                            if (block.Content.Annotations?.Count > 0)
                                content.Annotations = block
                                    .Content.Annotations.Concat(content.Annotations ?? [])
                                    .DistinctBy(annotation => JsonSerializer.Serialize(annotation, JsonOptions))
                                    .ToList();
                            block!.Content = content;
                        }
                        else
                        {
                            block!.Content = content;
                            block.Text = null;
                        }
                        output.Contents.Add(content);
                        break;
                    case AgentMessageOperationKind.PutMessage:
                    case AgentMessageOperationKind.SealMessage:
                        if (
                            operation.Kind == AgentMessageOperationKind.PutMessage
                            || operation.Header.Contents.Count > 0
                        )
                        {
                            var replacement = new Dictionary<string, BlockEntry>(StringComparer.Ordinal);
                            foreach (
                                var (value, index) in operation.Header.Contents.Select((value, index) => (value, index))
                            )
                            {
                                var copy = CloneContent(value);
                                var id =
                                    copy.AdditionalProperties?.GetValueOrDefault("blockId")?.ToString()
                                    ?? $"block:{index}";
                                copy.AdditionalProperties ??= [];
                                copy.AdditionalProperties["blockId"] = id;
                                if (!replacement.TryAdd(id, new BlockEntry { Content = copy }))
                                    throw new AgwException(ErrorCodes.InvalidParam);
                            }
                            // A snapshot naming blocks this entry already knows speaks the same block
                            // identity, so blocks it leaves out were merely omitted and survive in place.
                            // A snapshot sharing no identity is a full restatement and replaces everything.
                            var sharesIdentity = replacement.Keys.Any(entry.Blocks.ContainsKey);
                            var merged = (
                                sharesIdentity
                                    ? entry.Blocks.Select(block =>
                                        KeyValuePair.Create(
                                            block.Key,
                                            replacement.GetValueOrDefault(block.Key) ?? block.Value
                                        )
                                    )
                                    : []
                            )
                                .Concat(
                                    replacement.Where(block => !sharesIdentity || !entry.Blocks.ContainsKey(block.Key))
                                )
                                .ToArray();
                            entry.Blocks.Clear();
                            foreach (var (id, mergedBlock) in merged)
                            {
                                entry.Blocks.Add(id, mergedBlock);
                                output.Contents.Add(ReadBlock(mergedBlock));
                            }
                        }
                        if (operation.Kind == AgentMessageOperationKind.SealMessage)
                        {
                            if (operation.State == ConversationMessageState.Open || !Enum.IsDefined(operation.State))
                                throw new AgwException(ErrorCodes.InvalidParam);
                            entry.State = operation.State;
                        }
                        break;
                }
                // A message without content blocks is not persistable history; it only becomes
                // pending once it carries something to store.
                entry.Dirty |= entry.Blocks.Count > 0;
                if (operation.EventId != null)
                    _events.Add(operation.EventId, eventSignature!);
                entry.Cached = null;
                Stamp(output, entry, operation.Kind.ToString());
                Operations.Add(1, new KeyValuePair<string, object?>("outcome", "accepted"));
                return output;
            }
            catch
            {
                Operations.Add(1, new KeyValuePair<string, object?>("outcome", "conflict"));
                throw;
            }
            finally
            {
                Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    /// <summary>
    /// <para>按身份和序号定位逻辑消息，缺少时新建；调用方已持有 <see cref="_gate"/>。</para>
    /// <para>Locates a logical message by identity and index, creating it when absent. Callers hold
    /// <see cref="_gate"/>.</para>
    /// </summary>
    private MessageEntry ResolveEntry(
        string identity,
        int index,
        string purpose,
        AgentMessageOperation operation,
        Guid? canonicalId
    )
    {
        var key = $"{identity}#{index}";
        if (_messages.TryGetValue(key, out var existing))
            return existing;
        var entry = new MessageEntry
        {
            Id = canonicalId ?? Guid.CreateVersion7(),
            SourceId = operation.SourceId,
            Identity = identity,
            Index = index,
            Key = key,
            Purpose = purpose,
            Header = Clone(operation.Header),
            CreatedAt = operation.Header.CreatedAt ?? _timeProvider.GetUtcNow(),
        };
        entry.Header.Contents.Clear();
        entry.Header.AdditionalProperties ??= [];
        entry.Header.AdditionalProperties["messagePurpose"] = purpose;
        _messages.Add(key, entry);
        _sequences[identity] = index;
        return entry;
    }

    internal IReadOnlyList<ChatMessage> SealOpen(ConversationMessageState state)
    {
        lock (_gate)
        {
            // Materialize before applying: sealing targets known entries and must not observe
            // the dictionary it is iterating.
            return _messages
                .Values.Where(entry => entry.State == ConversationMessageState.Open)
                .ToArray()
                .Select(entry =>
                    Apply(
                        new AgentMessageOperation
                        {
                            SourceId = entry.SourceId,
                            Header = entry.Header,
                            Kind = AgentMessageOperationKind.SealMessage,
                            State = state,
                        },
                        entry
                    )
                )
                .OfType<ChatMessage>()
                .ToArray();
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
        {
            return _messages
                .Values.Where(entry => entry.Dirty)
                .Select(entry => entry.Cached ??= Snapshot(entry))
                .ToArray();
        }
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

    private static bool SameContents(MessageEntry entry, ChatMessage incoming)
    {
        if (entry.Blocks.Count != incoming.Contents.Count)
            return false;
        var index = 0;
        foreach (var block in entry.Blocks.Values)
        {
            var existing = CloneContent(block.Content);
            if (block.Text != null)
            {
                if (existing is TextContent text)
                    text.Text = block.Text.ToString();
                else if (existing is TextReasoningContent reasoning)
                    reasoning.Text = block.Text.ToString();
            }
            var candidate = CloneContent(incoming.Contents[index]);
            candidate.AdditionalProperties ??= [];
            candidate.AdditionalProperties["blockId"] =
                candidate.AdditionalProperties.GetValueOrDefault("blockId")?.ToString() ?? $"block:{index}";
            if (
                !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(existing, JsonOptions),
                    JsonSerializer.SerializeToElement(candidate, JsonOptions)
                )
            )
                return false;
            index++;
        }
        return true;
    }

    private ConversationMessageSnapshot Snapshot(MessageEntry entry)
    {
        var message = Clone(entry.Header);
        message.Contents = entry
            .Blocks.Values.Select(block =>
            {
                var content = CloneContent(block.Content);
                if (block.Text != null)
                {
                    if (content is TextContent text)
                        text.Text = block.Text.ToString();
                    else if (content is TextReasoningContent thinking)
                        thinking.Text = block.Text.ToString();
                }
                return content;
            })
            .ToList();
        Stamp(message, entry, "PutMessage");
        if (entry.State != ConversationMessageState.Completed)
            ConversationHistoryMetadata.ExcludeFromModelHistory(message);
        return new ConversationMessageSnapshot
        {
            MessageId = entry.Id,
            State = entry.State,
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

    private void Stamp(ChatMessage message, MessageEntry entry, string operation)
    {
        message.MessageId = entry.Id.ToString("D");
        message.CreatedAt = entry.CreatedAt;
        message.AdditionalProperties ??= [];
        message.AdditionalProperties[MessageTimestampMetadata.CreatedAtKey] = entry.CreatedAt;
        message.AdditionalProperties["messageState"] = entry.State.ToString().ToLowerInvariant();
        message.AdditionalProperties["messageOperation"] = operation;
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

    private static void MergeHeader(ChatMessage target, ChatMessage incoming)
    {
        if (incoming.AdditionalProperties == null)
            return;
        target.AdditionalProperties ??= [];
        foreach (var (key, value) in incoming.AdditionalProperties)
            target.AdditionalProperties[key] = value;
    }

    internal static ChatMessage Clone(ChatMessage value) =>
        JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), JsonOptions)!;

    internal static AIContent CloneContent(AIContent value) =>
        JsonSerializer.Deserialize<AIContent>(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), JsonOptions)!;

    private static AIContent ReadBlock(BlockEntry block)
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
    }

    private sealed class MessageEntry
    {
        public required Guid Id { get; init; }
        public required string SourceId { get; init; }
        public required string Identity { get; init; }
        public required int Index { get; init; }
        public required string Key { get; init; }
        public required string Purpose { get; init; }
        public required ChatMessage Header { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public Dictionary<string, BlockEntry> Blocks { get; } = new(StringComparer.Ordinal);
        public bool Dirty { get; set; }
        public ConversationMessageState State { get; set; }
        public ConversationMessageSnapshot? Cached { get; set; }
    }

    private sealed class BlockEntry
    {
        public required AIContent Content { get; set; }
        public StringBuilder? Text { get; set; }
    }
}
