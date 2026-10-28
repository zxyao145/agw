using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.Protocol;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// System Agent 的消息适配：保留模型响应的正文、推理与函数调用；工具结果在 Step 边界由 AgwChatHistoryProvider 写入，审批请求不进入历史。
/// Message adapter of System Agents: keeps text, reasoning and function calls of model responses; tool results are written by AgwChatHistoryProvider at the Step boundary, and approval requests stay out of history.
/// </summary>
internal class ModelMessageAdapter : IAgentMessageAdapter<AgentResponseUpdate>
{
    private static readonly string[] BlockIds = Enumerable.Range(0, 64).Select(index => $"block:{index}").ToArray();

    // ValueTuple 键按字段比较，每个增量不必拼接字符串。
    // ValueTuple keys compare by field, so no string is concatenated per delta.
    private readonly Dictionary<(string SourceId, string Purpose), (ChatRole Role, string? Author)> _headers = [];
    private readonly Dictionary<
        (string SourceId, string Role, string? Author, string Purpose),
        (Type Type, bool Opaque, int Index)
    > _tails = [];

    /// <summary>
    /// 为真时工具结果属于 Engine 事件并写入历史，且 System、User、Tool 消息只用于展示。
    /// When true, tool results are Engine events written to history, and System, User and Tool messages are display-only.
    /// </summary>
    protected virtual bool IsExternalEngine => false;

    /// <summary>
    /// External Engine 的流式输出完整覆盖消息内容；System Agent 的流式输出可能缺少被审批转换的函数调用，由完整响应校准。
    /// An External Engine's stream fully covers message content; a System Agent's stream may lack function calls converted for approval, so the complete response calibrates it.
    /// </summary>
    public bool StreamIsAuthoritative => IsExternalEngine;

    public virtual ChatMessage? Map(AgentResponseUpdate update)
    {
        var message = ToMessage(update);
        if (IsExcluded(message))
            return null;
        var id = GetSourceId(message);
        var headerKey = (id, AgentMessageProjection.GetPurpose(message));
        if (_headers.TryGetValue(headerKey, out var previous))
        {
            message.Role = update.Role ?? previous.Role;
            message.AuthorName ??= previous.Author;
        }
        _headers[headerKey] = (message.Role, message.AuthorName);
        message.MessageId = id;
        message.Contents = [];
        foreach (var content in update.Contents.Where(Records))
        {
            var copy = AgentMessageProjection.CloneContent(content);
            copy.AdditionalProperties ??= [];
            copy.AdditionalProperties["blockId"] = GetBlockId(update, content, id, message);
            message.Contents.Add(copy);
        }
        if (message.Contents.Count == 0)
            return null;
        MarkDisplayOnly(message);
        return message;
    }

    /// <summary>
    /// 同一批消息中重复的来源身份按出现次序区分，第一条沿用原始身份以校准增量；同一批再次校准时得到相同身份。
    /// Repeated source identities within one batch are told apart by occurrence, and the first keeps the original identity to calibrate deltas; calibrating the same batch again yields the same identities.
    /// </summary>
    public virtual IReadOnlyList<ChatMessage> MapComplete(IEnumerable<ChatMessage> messages)
    {
        var result = new List<ChatMessage>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var source in messages)
        {
            if (IsExcluded(source))
                continue;
            // 历史是协议状态：空推理可能带签名，只去掉空白文本。
            // History is protocol state: empty reasoning may carry a signature, so only blank text is removed.
            var contents = source
                .Contents.Where(Records)
                .Where(content => content is not TextContent text || !string.IsNullOrWhiteSpace(text.Text))
                .Select(AgentMessageProjection.CloneContent)
                .ToList();
            if (contents.Count == 0)
                continue;
            var message = AgentMessageProjection.Clone(source);
            message.RawRepresentation = null;
            message.Contents = contents;
            var sourceId = GetSourceId(message);
            var key = $"{sourceId}:{AgentMessageProjection.GetPurpose(message)}";
            var occurrence = occurrences.GetValueOrDefault(key);
            occurrences[key] = occurrence + 1;
            message.MessageId = occurrence == 0 ? sourceId : $"{sourceId}#{occurrence}";
            MarkDisplayOnly(message);
            result.Add(message);
        }
        return result;
    }

    /// <summary>
    /// 判断内容是否写入历史：使用量走遥测，System Agent 的工具结果与审批请求由其他路径处理。
    /// Whether content is written to history: usage goes to telemetry, and System Agent tool results and approval requests take other paths.
    /// </summary>
    public virtual bool Records(AIContent content) =>
        content is not UsageContent
        && (IsExternalEngine || content is not (FunctionResultContent or ToolApprovalRequestContent));

    protected virtual string GetBlockId(
        AgentResponseUpdate update,
        AIContent content,
        string sourceId,
        ChatMessage header
    )
    {
        if (content.AdditionalProperties?.GetValueOrDefault("blockId")?.ToString() is { Length: > 0 } explicitId)
            return explicitId;

        var key = (sourceId, header.Role.Value, header.AuthorName, AgentMessageProjection.GetPurpose(header));
        if (!_tails.TryGetValue(key, out var tail))
            tail = (content.GetType(), content is TextReasoningContent { ProtectedData: not null }, 0);
        else if (
            tail.Type != content.GetType()
            || tail.Opaque != (content is TextReasoningContent { ProtectedData: not null })
            || content is not (TextContent or TextReasoningContent)
        )
            tail = (content.GetType(), content is TextReasoningContent { ProtectedData: not null }, tail.Index + 1);
        _tails[key] = tail;
        return FormatBlockId(tail.Index);
    }

    protected virtual string GetSourceId(ChatMessage message) => SourceId(message);

    /// <summary>
    /// 常用的块序号复用预先生成的字符串。
    /// Common block indexes reuse pregenerated strings.
    /// </summary>
    protected static string FormatBlockId(int index) =>
        (uint)index < (uint)BlockIds.Length ? BlockIds[index] : $"block:{index}";

    /// <summary>
    /// 不写入历史的消息：临时副本、交接前缀与工具状态消息（由工具状态持久化单独写入）。
    /// Messages kept out of history: transient copies, handoff prefixes and tool status messages (written separately by tool state persistence).
    /// </summary>
    private static bool IsExcluded(ChatMessage message) =>
        ConversationHistoryMetadata.IsPersistenceExcluded(message)
        || ConversationHandoffMetadata.IsHandoffMessage(message)
        || message.AdditionalProperties.IsToolMessage();

    /// <summary>
    /// 含致命错误的消息只用于展示；External Engine 的 System、User、Tool 消息同样只用于展示。
    /// Messages carrying a fatal error are display-only, as are System, User and Tool messages of External Engines.
    /// </summary>
    private void MarkDisplayOnly(ChatMessage message)
    {
        if (
            message.Contents.OfType<ErrorContent>().Any(IsFatalError)
            || IsExternalEngine
                && (message.Role == ChatRole.System || message.Role == ChatRole.User || message.Role == ChatRole.Tool)
        )
            ConversationHistoryMetadata.ExcludeFromModelHistory(message);
    }

    /// <summary>
    /// SDK 写入的是 bool；经过投影的 JSON 复制后是 JsonElement。
    /// SDKs write a bool; after the projection's JSON copy it is a JsonElement.
    /// </summary>
    internal static bool IsFatalError(ErrorContent error) =>
        error.AdditionalProperties?.GetValueOrDefault("isFatalError") switch
        {
            bool fatal => fatal,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            _ => false,
        };

    private static string SourceId(ChatMessage message)
    {
        var source = message.AdditionalProperties?.GetValueOrDefault("sourceMessageId")?.ToString();
        if (!string.IsNullOrWhiteSpace(source))
            return source;
        if (!string.IsNullOrWhiteSpace(message.MessageId))
            return message.MessageId;
        if (message.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { CallId.Length: > 0 } result)
            return $"tool:{result.CallId}";
        return AgentMessageProjection.GetPurpose(message) == AgwMessageClassifier.ResultType
                ? AgwMessageClassifier.ResultType
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

/// <summary>
/// Pi 的消息适配：工具结果是 Pi 事件；System、User、Tool 消息只用于展示，Pi 自己持有模型侧历史。
/// Message adapter of Pi: tool results are Pi events; System, User and Tool messages are display-only because Pi owns its model-side history.
/// </summary>
internal sealed class PiMessageAdapter : ModelMessageAdapter
{
    protected override bool IsExternalEngine => true;
}

/// <summary>
/// Codex 的消息适配：按 item 划分消息边界，Turn 事件不进入历史。
/// Message adapter of Codex: item IDs bound messages, and turn events stay out of history.
/// </summary>
internal sealed class CodexMessageAdapter : ModelMessageAdapter
{
    private readonly HashSet<ItemIdentity> _completedFinalAnswers = [];

    protected override bool IsExternalEngine => true;

    public override ChatMessage? Map(AgentResponseUpdate update)
    {
        if (update.AdditionalProperties?.GetValueOrDefault("type")?.ToString() is "turn.started" or "turn.completed")
            return null;

        foreach (var (notification, item) in GetFinalAnswers(update.RawRepresentation))
        {
            if (string.IsNullOrWhiteSpace(item.Text))
                continue;

            var identity = new ItemIdentity(
                notification.ThreadId ?? GetPropertyString(update.AdditionalProperties, "threadId"),
                notification.TurnId ?? GetPropertyString(update.AdditionalProperties, "turnId"),
                item.Id
            );
            if (!_completedFinalAnswers.Add(identity))
                return null;
            var messageProperties = CopyProperties(update.AdditionalProperties);
            SetIdentityProperties(messageProperties, identity);
            messageProperties[AgwMessageClassifier.TypeKey] = AgwMessageClassifier.ResultType;
            messageProperties[AgwMessageClassifier.ResultSourceMessageIdKey] = FormatSourceId(identity);
            return base.Map(
                new AgentResponseUpdate(
                    ChatRole.Assistant,
                    [new TextContent(item.Text) { AdditionalProperties = CreateContentProperties(identity, 0) }]
                )
                {
                    MessageId = update.MessageId,
                    AuthorName = update.AuthorName,
                    CreatedAt = update.CreatedAt,
                    AdditionalProperties = messageProperties,
                }
            );
        }

        var message = base.Map(update);
        if (message != null)
            PreserveImagePathMetadata(update.Contents, message.Contents, update.RawRepresentation);
        return message;
    }

    public override IReadOnlyList<ChatMessage> MapComplete(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> completeMessages = [];
        var sourceIndex = 0;
        foreach (var source in messages)
        {
            Dictionary<string, ChatMessage> groupsByKey = new(StringComparer.Ordinal);
            List<ChatMessage> groups = [];
            foreach (
                var content in source
                    .Contents.Where(Records)
                    .Where(content => content is not TextContent text || !string.IsNullOrWhiteSpace(text.Text))
            )
            {
                var identity = GetIdentity(source, content);
                var key = GetGroupKey(identity, sourceIndex);
                if (!groupsByKey.TryGetValue(key, out var group))
                {
                    group = CreateGroup(source, identity);
                    groupsByKey.Add(key, group);
                    groups.Add(group);
                }

                var copy = AgentMessageProjection.CloneContent(content);
                if (content is DataContent sourceImage && copy is DataContent copiedImage)
                    PreserveImagePathMetadata(sourceImage, copiedImage, source.RawRepresentation);
                AddIndexedBlockId(
                    copy,
                    GetContentIndex(content.AdditionalProperties),
                    GetPropertyString(content.AdditionalProperties, "reasoningKind")
                );
                group.Contents.Add(copy);
            }

            HashSet<string> completedItems = new(StringComparer.Ordinal);
            Dictionary<string, ChatMessage> resultsByGroupKey = new(StringComparer.Ordinal);
            foreach (var (notification, item) in GetFinalAnswers(source.RawRepresentation))
            {
                if (string.IsNullOrWhiteSpace(item.Text))
                    continue;

                var identity = new ItemIdentity(
                    notification.ThreadId ?? GetPropertyString(source.AdditionalProperties, "threadId"),
                    notification.TurnId ?? GetPropertyString(source.AdditionalProperties, "turnId"),
                    item.Id
                );
                var key = GetGroupKey(identity, sourceIndex);
                if (!completedItems.Add(key))
                    continue;
                if (!groupsByKey.TryGetValue(key, out var group))
                {
                    group = CreateGroup(source, identity);
                    groupsByKey.Add(key, group);
                    groups.Add(group);
                }

                group.AdditionalProperties ??= [];
                SetIdentityProperties(group.AdditionalProperties, identity);
                ReplaceFinalText(group, item.Text, identity);
                resultsByGroupKey[key] = CreateResult(group, item.Text, identity);
            }

            foreach (var group in groups)
            {
                var key = GetGroupKey(GetIdentity(group, group.Contents.FirstOrDefault()), sourceIndex);
                completeMessages.Add(CoalesceCompleteContents(group));
                if (resultsByGroupKey.TryGetValue(key, out var result))
                    completeMessages.Add(result);
            }
            sourceIndex++;
        }

        return base.MapComplete(completeMessages);
    }

    protected override string GetSourceId(ChatMessage message)
    {
        var content = message.Contents.FirstOrDefault(content =>
            !string.IsNullOrWhiteSpace(GetPropertyString(content.AdditionalProperties, "itemId"))
        );
        var identity = GetIdentity(message, content);
        return identity.ItemId is { Length: > 0 } ? FormatSourceId(identity) : base.GetSourceId(message);
    }

    protected override string GetBlockId(
        AgentResponseUpdate update,
        AIContent content,
        string sourceId,
        ChatMessage header
    )
    {
        var explicitId = GetPropertyString(content.AdditionalProperties, "blockId");
        if (explicitId is { Length: > 0 })
            return explicitId;

        var contentIndex =
            GetContentIndex(content.AdditionalProperties) ?? GetContentIndex(update.AdditionalProperties);
        var reasoningKind =
            GetPropertyString(content.AdditionalProperties, "reasoningKind")
            ?? GetPropertyString(update.AdditionalProperties, "reasoningKind");
        return contentIndex.HasValue ? FormatCodexBlockId(contentIndex.Value, reasoningKind)
            : reasoningKind is { Length: > 0 } ? FormatCodexBlockId(0, reasoningKind)
            : base.GetBlockId(update, content, sourceId, header);
    }

    private static ChatMessage CreateGroup(ChatMessage source, ItemIdentity identity)
    {
        var properties = CopyProperties(source.AdditionalProperties);
        SetIdentityProperties(properties, identity);
        return new ChatMessage(source.Role, [])
        {
            MessageId = source.MessageId,
            AuthorName = source.AuthorName,
            CreatedAt = source.CreatedAt,
            AdditionalProperties = properties,
        };
    }

    private ChatMessage CoalesceCompleteContents(ChatMessage message)
    {
        Dictionary<(string BlockId, Type ContentType), AIContent> textBlocks = [];
        List<AIContent> contents = [];
        foreach (var content in message.Contents)
        {
            var blockId = GetPropertyString(content.AdditionalProperties, "blockId");
            if (
                blockId is not { Length: > 0 }
                || content is not (TextContent or TextReasoningContent { ProtectedData: null })
            )
            {
                contents.Add(content);
                continue;
            }

            var key = (blockId, content.GetType());
            if (!textBlocks.TryGetValue(key, out var existing))
            {
                textBlocks.Add(key, content);
                contents.Add(content);
                continue;
            }

            if (existing is TextContent targetText && content is TextContent nextText)
                targetText.Text += nextText.Text;
            else if (existing is TextReasoningContent targetReasoning && content is TextReasoningContent nextReasoning)
                targetReasoning.Text += nextReasoning.Text;

            existing.AdditionalProperties ??= [];
            foreach (var (name, value) in content.AdditionalProperties ?? [])
                existing.AdditionalProperties[name] = value;
            if (content.Annotations?.Count > 0)
                existing.Annotations = [.. existing.Annotations ?? [], .. content.Annotations];
        }

        message.Contents = contents;
        return message;
    }

    private static void ReplaceFinalText(ChatMessage message, string text, ItemIdentity identity)
    {
        var firstTextIndex = message.Contents.ToList().FindIndex(content => content is TextContent);
        var properties =
            firstTextIndex >= 0
                ? CopyProperties(message.Contents[firstTextIndex].AdditionalProperties)
                : CreateContentProperties(identity, 0);
        for (var index = message.Contents.Count - 1; index >= 0; index--)
        {
            if (message.Contents[index] is TextContent)
                message.Contents.RemoveAt(index);
        }

        if (GetContentIndex(properties) == null)
            properties["contentIndex"] = 0;
        var finalText = new TextContent(text) { AdditionalProperties = properties };
        AddIndexedBlockId(finalText, GetContentIndex(properties));
        message.Contents.Insert(
            firstTextIndex < 0 ? message.Contents.Count : Math.Min(firstTextIndex, message.Contents.Count),
            finalText
        );
    }

    private static ChatMessage CreateResult(ChatMessage source, string text, ItemIdentity identity)
    {
        var properties = CopyProperties(source.AdditionalProperties);
        SetIdentityProperties(properties, identity);
        properties[AgwMessageClassifier.TypeKey] = AgwMessageClassifier.ResultType;
        properties[AgwMessageClassifier.ResultSourceMessageIdKey] = FormatSourceId(identity);
        var content = new TextContent(text) { AdditionalProperties = CreateContentProperties(identity, 0) };
        AddIndexedBlockId(content, 0);
        return new ChatMessage(source.Role, [content])
        {
            MessageId = source.MessageId,
            AuthorName = source.AuthorName,
            CreatedAt = source.CreatedAt,
            AdditionalProperties = properties,
        };
    }

    private static ItemIdentity GetIdentity(ChatMessage message, AIContent? content) =>
        new(
            GetPropertyString(content?.AdditionalProperties, "threadId")
                ?? GetPropertyString(message.AdditionalProperties, "threadId"),
            GetPropertyString(content?.AdditionalProperties, "turnId")
                ?? GetPropertyString(message.AdditionalProperties, "turnId"),
            GetPropertyString(content?.AdditionalProperties, "itemId")
                ?? GetPropertyString(message.AdditionalProperties, "itemId")
        );

    private static ItemIdentity GetIdentity(DataContent content) =>
        new(
            GetPropertyString(content.AdditionalProperties, "threadId"),
            GetPropertyString(content.AdditionalProperties, "turnId"),
            GetPropertyString(content.AdditionalProperties, "itemId")
        );

    private static void PreserveImagePathMetadata(
        IEnumerable<AIContent> sourceContents,
        IEnumerable<AIContent> mappedContents,
        object? sourceRawRepresentation
    )
    {
        var mappedImages = mappedContents.OfType<DataContent>().ToList();
        foreach (var sourceImage in sourceContents.OfType<DataContent>())
        {
            var identity = GetIdentity(sourceImage);
            if (!HasCompleteIdentity(identity))
                continue;

            var imagePath = GetImagePath(identity, sourceImage.RawRepresentation, sourceRawRepresentation);
            if (imagePath == null)
                continue;

            foreach (var mappedImage in mappedImages.Where(image => GetIdentity(image) == identity))
            {
                mappedImage.AdditionalProperties ??= [];
                mappedImage.AdditionalProperties["imagePath"] = imagePath;
            }
        }
    }

    private static void PreserveImagePathMetadata(
        DataContent sourceImage,
        DataContent mappedImage,
        object? sourceRawRepresentation
    )
    {
        var identity = GetIdentity(sourceImage);
        if (!HasCompleteIdentity(identity) || GetIdentity(mappedImage) != identity)
            return;

        var imagePath = GetImagePath(identity, sourceImage.RawRepresentation, sourceRawRepresentation);
        if (imagePath == null)
            return;

        mappedImage.AdditionalProperties ??= [];
        mappedImage.AdditionalProperties["imagePath"] = imagePath;
    }

    private static string? GetImagePath(
        ItemIdentity identity,
        object? contentRawRepresentation,
        object? sourceRawRepresentation
    )
    {
        foreach (
            var notification in GetNotifications(contentRawRepresentation)
                .Concat(GetNotifications(sourceRawRepresentation))
        )
        {
            if (
                notification.Method == "item/completed"
                && string.Equals(notification.ThreadId, identity.ThreadId, StringComparison.Ordinal)
                && string.Equals(notification.TurnId, identity.TurnId, StringComparison.Ordinal)
                && notification.Parameters is ItemCompletedNotification { Item: ImageViewThreadItem item }
                && string.Equals(item.Id, identity.ItemId, StringComparison.Ordinal)
            )
                return item.Path;
        }

        return null;
    }

    private static bool HasCompleteIdentity(ItemIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.ThreadId)
        && !string.IsNullOrWhiteSpace(identity.TurnId)
        && !string.IsNullOrWhiteSpace(identity.ItemId);

    private static IEnumerable<(AppServerNotification Notification, AgentMessageThreadItem Item)> GetFinalAnswers(
        object? rawRepresentation
    )
    {
        foreach (var notification in GetNotifications(rawRepresentation))
        {
            if (
                notification.Method == "item/completed"
                && notification.Parameters is ItemCompletedNotification { Item: AgentMessageThreadItem item }
                && item.Phase.GetValueOrDefault()
                    is StringMessagePhase083A0BEF { Value: StringMessagePhaseValue083A0BEF.FinalAnswer }
            )
                yield return (notification, item);
        }
    }

    private static IEnumerable<AppServerNotification> GetNotifications(object? rawRepresentation)
    {
        if (rawRepresentation is AppServerNotification notification)
        {
            yield return notification;
            yield break;
        }

        if (rawRepresentation is not IEnumerable<AppServerNotification> notifications)
            yield break;

        foreach (var item in notifications)
            yield return item;
    }

    private static string GetGroupKey(ItemIdentity identity, int sourceIndex) =>
        identity.ItemId is { Length: > 0 } ? FormatSourceId(identity) : $"message:{sourceIndex}";

    private static string FormatSourceId(ItemIdentity identity) =>
        JsonSerializer.Serialize(new string?[] { "codex", identity.ThreadId, identity.TurnId, identity.ItemId });

    private static void SetIdentityProperties(AdditionalPropertiesDictionary properties, ItemIdentity identity)
    {
        if (identity.ThreadId != null)
            properties["threadId"] = identity.ThreadId;
        if (identity.TurnId != null)
            properties["turnId"] = identity.TurnId;
        if (identity.ItemId != null)
            properties["itemId"] = identity.ItemId;
    }

    private static AdditionalPropertiesDictionary CreateContentProperties(ItemIdentity identity, int contentIndex)
    {
        AdditionalPropertiesDictionary properties = [];
        SetIdentityProperties(properties, identity);
        properties["contentIndex"] = contentIndex;
        return properties;
    }

    private static void AddIndexedBlockId(AIContent content, int? contentIndex, string? reasoningKind = null)
    {
        if (
            (!contentIndex.HasValue && reasoningKind is not { Length: > 0 })
            || GetPropertyString(content.AdditionalProperties, "blockId") is { Length: > 0 }
        )
            return;

        content.AdditionalProperties ??= [];
        content.AdditionalProperties["blockId"] = FormatCodexBlockId(contentIndex ?? 0, reasoningKind);
    }

    private static string FormatCodexBlockId(int contentIndex, string? reasoningKind) =>
        reasoningKind is { Length: > 0 } ? $"block:{reasoningKind}:{contentIndex}" : FormatBlockId(contentIndex);

    private static int? GetContentIndex(AdditionalPropertiesDictionary? properties)
    {
        var value = properties?.GetValueOrDefault("contentIndex");
        if (value is int index)
            return index;
        if (value is long longIndex && longIndex is >= int.MinValue and <= int.MaxValue)
            return (int)longIndex;
        if (value is JsonElement { ValueKind: JsonValueKind.Number } element && element.TryGetInt32(out var jsonIndex))
            return jsonIndex;
        return null;
    }

    private static string? GetPropertyString(AdditionalPropertiesDictionary? properties, string key) =>
        properties?.GetValueOrDefault(key)?.ToString() is { Length: > 0 } value ? value : null;

    private static AdditionalPropertiesDictionary CopyProperties(AdditionalPropertiesDictionary? properties) =>
        properties == null ? [] : new(properties);

    private readonly record struct ItemIdentity(string? ThreadId, string? TurnId, string? ItemId);
}

/// <summary>
/// Claude Code 的消息适配：过滤传输事件，按原生内容块索引划分文本块；完整消息先按消息身份合并片段。
/// Message adapter of Claude Code: filters transport events and bounds text blocks by native content-block index; complete messages first merge their fragments by message identity.
/// </summary>
internal sealed class ClaudeMessageAdapter : ModelMessageAdapter
{
    protected override bool IsExternalEngine => true;

    public override ChatMessage? Map(AgentResponseUpdate update) =>
        ClaudeCodeMessagePolicy.IsTransportEvent(update.Role, update.AdditionalProperties, update.Contents)
            ? null
            : base.Map(update);

    public override IReadOnlyList<ChatMessage> MapComplete(IEnumerable<ChatMessage> messages)
    {
        var result = base.MapComplete(Coalesce(messages));
        foreach (var message in result.Where(IsSyntheticAssistantError))
            ConversationHistoryMetadata.ExcludeFromModelHistory(message);
        return result;
    }

    protected override string GetBlockId(
        AgentResponseUpdate update,
        AIContent content,
        string sourceId,
        ChatMessage header
    ) =>
        (content.RawRepresentation ?? update.RawRepresentation) is StreamEvent stream
        && stream.Event.TryGetProperty("index", out var index)
        && index.TryGetInt32(out var value)
            ? FormatBlockId(value)
            : base.GetBlockId(update, content, sourceId, header);

    /// <summary>
    /// 按消息身份合并同一条消息的全部片段，保留首次出现的顺序；穿插的通知与工具结果保留各自位置。
    /// Merges every fragment of one message by message identity in order of first appearance; interleaved notifications and tool results keep their own places.
    /// </summary>
    private static IEnumerable<ChatMessage> Coalesce(IEnumerable<ChatMessage> messages)
    {
        List<List<ChatMessage>> groups = [];
        Dictionary<(string Id, string? Author, bool Result, string? Type), int> groupIndexes = [];
        foreach (var message in messages)
        {
            if (ClaudeCodeMessagePolicy.IsTransportEvent(message.Role, message.AdditionalProperties, message.Contents))
                continue;

            if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.MessageId))
            {
                var key = (
                    message.MessageId,
                    message.AuthorName,
                    AgwMessageClassifier.IsResult(message),
                    message.AdditionalProperties?.GetValueOrDefault(AgwMessageClassifier.TypeKey)?.ToString()
                );
                if (groupIndexes.TryGetValue(key, out var index))
                {
                    groups[index].Add(message);
                    continue;
                }
                groupIndexes[key] = groups.Count;
            }
            groups.Add([message]);
        }
        foreach (var fragments in groups)
            yield return fragments.Count == 1
                ? fragments[0]
                : new ChatResponse(fragments).ToChatResponseUpdates().ToChatResponse().Messages[0];
    }

    private static bool IsSyntheticAssistantError(ChatMessage message) =>
        message.Role == ChatRole.Assistant
        && string.Equals(message.AuthorName, "<synthetic>", StringComparison.Ordinal)
        && message.Contents.Count > 0
        && message.Contents.All(content => content is ErrorContent);
}
