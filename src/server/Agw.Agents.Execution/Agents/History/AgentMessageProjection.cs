using System.Text;
using System.Text.Json;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// 一种 Engine 的原生事件到历史消息的映射；完整消息在校准前按同一规则整理。
/// Maps one Engine's native events to history messages; complete messages are prepared by the same rules before calibration.
/// </summary>
internal interface IAgentMessageAdapter<in TEvent>
{
    /// <summary>
    /// 为真时流式增量是消息的权威内容，完整消息只补充没有流式输出的消息；为假时完整消息校准流式内容。
    /// When true the streaming deltas are the authoritative content of a message and complete messages only add messages that were never streamed; when false complete messages calibrate streamed content.
    /// </summary>
    bool StreamIsAuthoritative { get; }

    ChatMessage? Map(TEvent nativeEvent);

    /// <summary>
    /// 判断一段内容是否写入历史；不写入的内容仍交给调用方。
    /// Whether one content item is written to history; content not written still reaches the caller.
    /// </summary>
    bool Records(AIContent content);

    /// <summary>
    /// 整理 SDK 回调给出的完整消息：合并片段、过滤传输内容，并赋予与增量相同的来源身份。
    /// Prepares complete messages from SDK callbacks: merges fragments, filters transport content and assigns the same source identity as the deltas.
    /// </summary>
    IReadOnlyList<ChatMessage> MapComplete(IEnumerable<ChatMessage> messages);
}

/// <summary>
/// <para>按消息和内容块身份累积追加内容，并提供持久化快照。</para>
/// <para>Accumulates appended content by message and block identity and supplies persistence snapshots.</para>
/// </summary>
internal sealed class AgentMessageProjection : IConversationMessageSource
{
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;
    private readonly Lock _gate = new();
    private readonly ConversationMessageWriteScope _scope;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<MessageIdentity, MessageEntry> _messages = [];
    private readonly Dictionary<MessageIdentity, MessageEntry> _aliases = [];
    private bool _completed;

    internal AgentMessageProjection(ConversationMessageWriteScope scope, TimeProvider timeProvider)
    {
        _scope = scope;
        _timeProvider = timeProvider;
    }

    internal ConversationMessageWriteScope Scope => _scope;

    /// <summary>
    /// 追加一段增量：同一消息的相同文本块按到达顺序拼接，其他内容按出现顺序保留。
    /// 消息已由完整内容写入时（SDK 可能先写入完整消息再输出增量），增量只交给调用方，不再改变存储内容。
    /// Appends one delta: text of the same block is concatenated in arrival order, other content is kept in order of appearance.
    /// Once a message was written with complete content (an SDK may write the complete message before emitting its deltas), deltas only reach the caller and leave the stored content unchanged.
    /// </summary>
    internal ChatMessage Append(ChatMessage message, bool newMessage = false, int? stepIndex = null)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId) || message.Contents.Count == 0)
            throw new AgwException(ErrorCodes.InvalidParam);

        lock (_gate)
        {
            var purpose = GetPurpose(message);
            var identity = new MessageIdentity(
                message.MessageId,
                message.Role.Value,
                message.AuthorName,
                purpose,
                stepIndex,
                newMessage ? Guid.CreateVersion7() : Guid.Empty
            );
            if (!_messages.TryGetValue(identity, out var entry) && !_aliases.TryGetValue(identity, out entry))
            {
                entry = newMessage
                    ? null
                    : _messages.Values.LastOrDefault(value =>
                        value.Completed
                        && value.SourceId == message.MessageId
                        && value.Purpose == purpose
                        && (value.StepIndex == null || value.StepIndex == stepIndex)
                    );
                if (entry != null)
                    _aliases.Add(identity, entry);
                else
                {
                    entry = CreateEntry(message, Guid.CreateVersion7(), message.MessageId, purpose, stepIndex);
                    _messages.Add(identity, entry);
                }
            }
            if (entry.Completed)
            {
                var passthrough = CopyHeader(entry.Header);
                passthrough.Contents = message.Contents.Select(CloneContent).ToList();
                Stamp(passthrough, entry);
                return passthrough;
            }
            entry.Streamed = true;
            MergeHeader(entry, message, purpose);
            var output = CopyHeader(entry.Header);
            foreach (var value in message.Contents)
            {
                var content = CloneContent(value);
                var blockId = content.AdditionalProperties?.GetValueOrDefault("blockId")?.ToString();
                var text = GetText(content);
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
                    // 输出与块各持有一份副本：输出交给调用方后可能被修改，块的属性随后续增量更新。
                    // The output and the block each hold a copy: the output may be changed after it reaches the caller, and the block's properties follow later deltas.
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

    /// <summary>
    /// 写入一条完整消息：MessageId 为 GUID 时它就是行 Id，重复写入更新同一行；同一来源的增量消息已存在时用完整内容校准它。
    /// replaceStreamed 为假时，已经流式输出的消息保持流式内容。
    /// Writes one complete message: a GUID MessageId is the row Id and repeated writes update the same row; an existing delta message of the same source is calibrated with the complete content.
    /// When replaceStreamed is false, a message that was already streamed keeps its streamed content.
    /// </summary>
    internal ChatMessage Put(ChatMessage message, int? stepIndex = null, bool replaceStreamed = true)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Contents.Count == 0)
            throw new AgwException(ErrorCodes.InvalidParam);

        lock (_gate)
        {
            var purpose = GetPurpose(message);
            var rowId = Guid.TryParse(message.MessageId, out var parsed) ? parsed : (Guid?)null;
            var entry = rowId is { } id ? _messages.Values.FirstOrDefault(value => value.Id == id) : null;
            entry ??= message.MessageId is { Length: > 0 } sourceId
                ? _messages.Values.LastOrDefault(value =>
                    value.SourceId == sourceId
                    && value.Purpose == purpose
                    && (stepIndex == null || value.StepIndex == stepIndex)
                )
                : null;
            if (entry is { Streamed: true } && !replaceStreamed)
                return (entry.Cached ??= Snapshot(entry)).Message;
            if (entry == null)
            {
                var entryId = rowId ?? Guid.CreateVersion7();
                entry = CreateEntry(message, entryId, message.MessageId ?? entryId.ToString("D"), purpose, stepIndex);
                _messages.Add(MessageIdentity.ForPut(entryId), entry);
            }
            MergeHeader(entry, message, purpose);
            entry.Blocks.Clear();
            entry.TextBlocks.Clear();
            foreach (var value in message.Contents)
            {
                var content = CloneContent(value);
                var text = GetText(content);
                entry.Blocks.Add(
                    new BlockEntry { Content = content, Text = text == null ? null : new StringBuilder(text) }
                );
            }
            entry.Completed = true;
            entry.Dirty = true;
            entry.Cached = null;
            return (entry.Cached = Snapshot(entry)).Message;
        }
    }

    /// <summary>
    /// 以持久化后的形式读取全部消息：每条消息经过一次 JSON 往返，调用方得到独立的副本。
    /// Reads every message in its persisted form: each message makes one JSON round trip, so callers get independent copies.
    /// </summary>
    internal IReadOnlyList<ChatMessage> ReadMessages()
    {
        lock (_gate)
            return _messages.Values.Select(entry => Clone((entry.Cached ??= Snapshot(entry)).Message)).ToArray();
    }

    internal void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            foreach (var entry in _messages.Values)
            {
                entry.Dirty = true;
                entry.Cached = null;
            }
        }
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

    private MessageEntry CreateEntry(ChatMessage message, Guid id, string sourceId, string purpose, int? stepIndex) =>
        new()
        {
            Id = id,
            SourceId = sourceId,
            Purpose = purpose,
            Header = CopyHeader(message),
            CreatedAt = message.CreatedAt ?? _timeProvider.GetUtcNow(),
            StepIndex = stepIndex,
        };

    private static void MergeHeader(MessageEntry entry, ChatMessage message, string purpose)
    {
        entry.Header.AdditionalProperties ??= [];
        foreach (var (key, value) in message.AdditionalProperties ?? [])
            entry.Header.AdditionalProperties[key] = value;
        entry.Header.AdditionalProperties["messagePurpose"] = purpose;
    }

    private static string? GetText(AIContent content) =>
        content switch
        {
            TextContent textContent => textContent.Text,
            TextReasoningContent { ProtectedData: null } reasoning => reasoning.Text,
            _ => null,
        };

    private ConversationMessageSnapshot Snapshot(MessageEntry entry)
    {
        var message = CopyHeader(entry.Header);
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
        if (!_completed && !entry.Completed)
            ConversationHistoryMetadata.ExcludeFromModelHistory(message);
        return new ConversationMessageSnapshot
        {
            MessageId = entry.Id,
            CreatedAt = entry.CreatedAt,
            Author = message.AuthorName,
            StepIndex = entry.StepIndex,
            IsResult = entry.Purpose == AgwMessageClassifier.ResultType,
            Message = message,
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
        if (entry.StepIndex != null)
            message.AdditionalProperties["stepIndex"] = entry.StepIndex.Value;
        if (entry.Purpose != AgwMessageClassifier.ResultType)
            return;
        // resultSourceMessageId 指向 Result 重复的正文消息：SDK 给出来源身份时换成它的行 Id，没有给出时取正文相同、没有工具调用的最近一条 Agent 消息。
        // resultSourceMessageId points to the message whose text the Result repeats: a source identity given by the SDK becomes its row Id, otherwise the latest Agent message with the same text and no tool call is used.
        var source = message
            .AdditionalProperties.GetValueOrDefault(AgwMessageClassifier.ResultSourceMessageIdKey)
            ?.ToString()
            is { } sourceId
            ? _messages.Values.FirstOrDefault(value =>
                value.SourceId == sourceId && value.Purpose != AgwMessageClassifier.ResultType
            )
            : _messages.Values.LastOrDefault(value =>
                value.Purpose != AgwMessageClassifier.ResultType
                && value.Header.Role == ChatRole.Assistant
                && value.Blocks.All(block => block.Content is not FunctionCallContent)
                && message.Text.Length > 0
                && string.Equals(EntryText(value), message.Text, StringComparison.Ordinal)
            );
        if (source != null)
            message.AdditionalProperties[AgwMessageClassifier.ResultSourceMessageIdKey] = source.Id.ToString("D");
    }

    private static string EntryText(MessageEntry entry) =>
        string.Concat(
            entry.Blocks.Where(block => block.Content is TextContent).Select(block => block.Text?.ToString())
        );

    internal static string GetPurpose(ChatMessage message) =>
        message.AdditionalProperties?.GetValueOrDefault("messagePurpose")?.ToString() == AgwMessageClassifier.ResultType
        || AgwMessageClassifier.IsResult(message)
            ? AgwMessageClassifier.ResultType
            : "message";

    internal static ChatMessage Clone(ChatMessage value) =>
        JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), JsonOptions)!;

    /// <summary>
    /// <para>复制一段内容。文本与推理是流式增量中最常见的内容，按类型直接复制可序列化的属性并复制属性字典与注释列表；
    /// 其他类型经过一次 JSON 往返。两种方式都不保留 RawRepresentation。</para>
    /// <para>Copies one content item. Text and reasoning are the most common streaming content, so their serializable properties are copied directly along with new property dictionaries and annotation lists;
    /// other types make one JSON round trip. Neither keeps RawRepresentation.</para>
    /// </summary>
    internal static AIContent CloneContent(AIContent value) =>
        value switch
        {
            TextContent text when text.GetType() == typeof(TextContent) => new TextContent(text.Text)
            {
                Annotations = CopyAnnotations(text.Annotations),
                AdditionalProperties = CopyProperties(text.AdditionalProperties),
            },
            TextReasoningContent reasoning when reasoning.GetType() == typeof(TextReasoningContent) =>
                new TextReasoningContent(reasoning.Text)
                {
                    ProtectedData = reasoning.ProtectedData,
                    Annotations = CopyAnnotations(reasoning.Annotations),
                    AdditionalProperties = CopyProperties(reasoning.AdditionalProperties),
                },
            _ => JsonSerializer.Deserialize<AIContent>(
                JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
                JsonOptions
            )!,
        };

    /// <summary>
    /// 复制消息头：角色、作者、ID、时间与新的属性字典，内容为空列表，不保留 RawRepresentation。
    /// Copies a message header: role, author, ID, time and a new property dictionary, with an empty content list and no RawRepresentation.
    /// </summary>
    private static ChatMessage CopyHeader(ChatMessage value) =>
        new()
        {
            Role = value.Role,
            AuthorName = value.AuthorName,
            MessageId = value.MessageId,
            CreatedAt = value.CreatedAt,
            AdditionalProperties = CopyProperties(value.AdditionalProperties),
        };

    private static AdditionalPropertiesDictionary? CopyProperties(AdditionalPropertiesDictionary? properties) =>
        properties == null ? null : new AdditionalPropertiesDictionary(properties);

    private static List<AIAnnotation>? CopyAnnotations(IList<AIAnnotation>? annotations) =>
        annotations == null ? null : [.. annotations];

    /// <summary>
    /// 流式增量归属的消息身份；Nonce 区分强制新建的消息，Put 写入的行以空 MessageId 与行 Id 登记。
    /// The message identity a streaming delta belongs to; Nonce distinguishes forced new messages, and rows written by Put register with an empty MessageId and their row Id.
    /// </summary>
    private readonly record struct MessageIdentity(
        string MessageId,
        string Role,
        string? Author,
        string Purpose,
        int? StepIndex,
        Guid Nonce
    )
    {
        public static MessageIdentity ForPut(Guid entryId) => new("", "", null, "", null, entryId);
    }

    private sealed class MessageEntry
    {
        public required Guid Id { get; init; }
        public required string SourceId { get; init; }
        public required string Purpose { get; init; }
        public required ChatMessage Header { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public int? StepIndex { get; init; }
        public List<BlockEntry> Blocks { get; } = [];
        public Dictionary<string, BlockEntry> TextBlocks { get; } = new(StringComparer.Ordinal);
        public bool Completed { get; set; }
        public bool Streamed { get; set; }
        public bool Dirty { get; set; }
        public ConversationMessageSnapshot? Cached { get; set; }
    }

    private sealed class BlockEntry
    {
        public required AIContent Content { get; init; }
        public StringBuilder? Text { get; init; }
    }
}
