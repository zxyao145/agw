using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using Agw.Projects.Contracts.History;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>MAF model deltas: a call is an explicit boundary when the provider omits its message ID.</summary>
internal class ModelMessageAdapter : IAgentMessageAdapter<AgentResponseUpdate>
{
    private readonly Dictionary<string, (ChatRole Role, string? Author)> _headers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Type Type, bool Opaque, int Index)> _tails = new(StringComparer.Ordinal);

    public virtual IReadOnlyList<AgentMessageOperation> Map(AgentResponseUpdate update)
    {
        var header = ToMessage(update);
        if (
            ConversationHistoryMetadata.IsPersistenceExcluded(header)
            || ConversationHandoffMetadata.IsHandoffMessage(header)
        )
            return [];
        var id = SourceId(header);
        StabilizeHeader(header, id, update.Role);
        var operations = new List<AgentMessageOperation>();
        foreach (var content in update.Contents.Where(content => content is not UsageContent))
        {
            var blockId = GetBlockId(update, content, id, header);
            operations.Add(
                new AgentMessageOperation
                {
                    SourceId = id,
                    Header = header,
                    Kind = content is TextContent or TextReasoningContent { ProtectedData: null }
                        ? AgentMessageOperationKind.AppendText
                        : AgentMessageOperationKind.PutBlock,
                    BlockId = blockId,
                    Content = content,
                }
            );
        }
        return operations;
    }

    public virtual IReadOnlyList<AgentMessageOperation> MapSnapshot(ChatMessage message) =>
        CreateSnapshotOperations(message, completed: true);

    public virtual IReadOnlyList<AgentMessageOperation> MapFinalization() => [];

    protected static IReadOnlyList<AgentMessageOperation> CreateSnapshotOperations(ChatMessage message, bool completed)
    {
        if (
            ConversationHistoryMetadata.IsPersistenceExcluded(message)
            || ConversationHandoffMetadata.IsHandoffMessage(message)
        )
            return [];
        var snapshot = message.Clone();
        snapshot.Contents = message.Contents.Where(content => content is not UsageContent).ToList();
        if (snapshot.Contents.Count == 0 && message.Contents.Count > 0)
            return [];
        // 完整消息缺少身份时独立保存；流式调用的身份由归并后的元数据传递。
        // Preserve unidentified snapshots independently; normalized streams carry their source identity.
        var operation = new AgentMessageOperation
        {
            SourceId = KnownSourceId(message) ?? Guid.CreateVersion7().ToString("D"),
            Header = snapshot,
            Kind = AgentMessageOperationKind.PutMessage,
        };
        return completed ? [operation, CreateSealOperation(operation)] : [operation];
    }

    /// <summary>Builds a header-only operation for updates that carry lifecycle instead of content.</summary>
    protected AgentMessageOperation? CreateHeaderOperation(AgentResponseUpdate update)
    {
        var header = ToMessage(update);
        if (
            ConversationHistoryMetadata.IsPersistenceExcluded(header)
            || ConversationHandoffMetadata.IsHandoffMessage(header)
        )
            return null;
        var id = SourceId(header);
        StabilizeHeader(header, id, update.Role);
        return new AgentMessageOperation
        {
            SourceId = id,
            Header = header,
            Kind = AgentMessageOperationKind.PutMessage,
        };
    }

    /// <summary>Carries a source's first known role and author onto later deltas that omit them.</summary>
    protected void StabilizeHeader(ChatMessage header, string sourceId, ChatRole? updateRole)
    {
        var headerKey = $"{sourceId}:{AgentMessageProjection.GetPurpose(header)}";
        if (_headers.TryGetValue(headerKey, out var previous))
        {
            header.Role = updateRole ?? previous.Role;
            header.AuthorName ??= previous.Author;
        }
        _headers[headerKey] = (header.Role, header.AuthorName);
    }

    protected static AgentMessageOperation CreateSealOperation(AgentMessageOperation operation)
    {
        var header = operation.Header.Clone();
        header.AdditionalProperties =
            operation.Header.AdditionalProperties == null ? [] : new(operation.Header.AdditionalProperties);
        header.AdditionalProperties["messagePurpose"] = AgentMessageProjection.GetPurpose(operation.Header);
        header.Contents = [];
        return operation with
        {
            Header = header,
            Kind = AgentMessageOperationKind.SealMessage,
            BlockId = null,
            Content = null,
            State = operation.Header.Contents.OfType<ErrorContent>().Any()
                ? ConversationMessageState.Failed
                : ConversationMessageState.Completed,
        };
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

        var key = $"{sourceId}:{header.Role}:{header.AuthorName}";
        if (!_tails.TryGetValue(key, out var tail))
            tail = (content.GetType(), content is TextReasoningContent { ProtectedData: not null }, 0);
        else if (
            tail.Type != content.GetType()
            || tail.Opaque != (content is TextReasoningContent { ProtectedData: not null })
            || content is not (TextContent or TextReasoningContent)
        )
            tail = (content.GetType(), content is TextReasoningContent { ProtectedData: not null }, tail.Index + 1);
        _tails[key] = tail;
        if (content is FunctionCallContent call)
            return $"call:{call.CallId}";
        if (content is FunctionResultContent result)
            return $"result:{result.CallId}";
        return $"block:{tail.Index}";
    }

    protected static string SourceId(ChatMessage message) =>
        KnownSourceId(message)
        ?? (
            AgentMessageProjection.GetPurpose(message) == "result" ? "result"
            : message.Role == ChatRole.Assistant ? "response"
            : Guid.CreateVersion7().ToString("D")
        );

    private static string? KnownSourceId(ChatMessage message)
    {
        var source = message.AdditionalProperties?.GetValueOrDefault("sourceMessageId")?.ToString();
        if (!string.IsNullOrWhiteSpace(source))
            return source;
        if (!string.IsNullOrWhiteSpace(message.MessageId))
            return message.MessageId;
        return message.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { CallId.Length: > 0 } result
            ? $"tool:{result.CallId}"
            : null;
    }

    internal static ChatMessage ToMessage(AgentResponseUpdate update) =>
        new(update.Role ?? ChatRole.Assistant, update.Contents)
        {
            MessageId = update.MessageId,
            AuthorName = update.AuthorName,
            CreatedAt = update.CreatedAt,
            AdditionalProperties = update.AdditionalProperties,
        };
}

internal sealed class CodexMessageAdapter : ModelMessageAdapter
{
    public override IReadOnlyList<AgentMessageOperation> Map(AgentResponseUpdate update)
    {
        var type = update.AdditionalProperties?.GetValueOrDefault("type")?.ToString();
        if (type is "turn.started" or "turn.completed")
            return [];
        // Text and reasoning use the common append path. System items describe current tool/plan state.
        var operations =
            update.Role == ChatRole.System && type is "item.started" or "item.updated" or "item.completed"
                ? CreateSnapshotOperations(ToMessage(update), completed: false)
                : base.Map(update);
        if (type != "item.completed" || operations.Count == 0)
            return operations;
        return [.. operations, CreateSealOperation(operations[0])];
    }
}

internal sealed class ClaudeMessageAdapter : ModelMessageAdapter
{
    private readonly HashSet<string> _historyMessages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentMessageOperation> _completedMessages = new(StringComparer.Ordinal);

    public override IReadOnlyList<AgentMessageOperation> Map(AgentResponseUpdate update)
    {
        if (ClaudeCodeMessagePolicy.IsTransportEvent(update.Role, update.AdditionalProperties, update.Contents))
            return [];
        var operations = base.Map(update);
        if (update.FinishReason == null && update.RawRepresentation is not AssistantMessage)
            return operations;
        // 复用 base.Map 已校准角色与作者的操作，确保封存与正文指向同一条消息。
        // Reuse the operation base.Map already stabilized so the seal targets the same logical
        // message as the streamed content instead of deriving a second identity from a bare header.
        if ((operations.FirstOrDefault() ?? CreateHeaderOperation(update)) is not { } target)
            return operations;
        var completed = CreateSealOperation(target);
        _completedMessages[completed.SourceId] = completed;
        // SDK 在发送 AssistantMessage 补齐内容前已将它加入历史回调。
        // The SDK includes AssistantMessage supplements in its preceding history callback.
        if (update.RawRepresentation is AssistantMessage && _historyMessages.Contains(completed.SourceId))
            return [completed];
        return operations;
    }

    public override IReadOnlyList<AgentMessageOperation> MapSnapshot(ChatMessage message)
    {
        var operations = CreateSnapshotOperations(message, completed: false);
        foreach (var operation in operations)
            _historyMessages.Add(operation.SourceId);
        return operations;
    }

    public override IReadOnlyList<AgentMessageOperation> MapFinalization() => _completedMessages.Values.ToArray();

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

internal sealed class PiMessageAdapter : ModelMessageAdapter
{
    public override IReadOnlyList<AgentMessageOperation> Map(AgentResponseUpdate update) =>
        update.AdditionalProperties?.GetValueOrDefault("messageSnapshot") is true
        || update.AdditionalProperties?.GetValueOrDefault("type")?.ToString() == "result"
            ? MapSnapshot(ToMessage(update))
            : base.Map(update);
}
