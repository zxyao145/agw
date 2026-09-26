using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Messaging;

/// <summary>
/// <para>合并同一消息相邻的流式文本增量。规则与客户端 execution-core 的 appendStreamingContents 一致：同一 blockId 的文本或推理追加正文，
/// 其他内容按顺序追加，消息与内容的属性由后到的值覆盖，CreatedAt 保留先到的值。客户端收到合并结果与依次收到各个增量得到相同的状态。</para>
/// <para>Merges adjacent streaming text deltas of one message. The rules match appendStreamingContents in the client's execution-core: text or reasoning of the same blockId
/// appends its body, other content is appended in order, later message and content properties overwrite earlier ones, and CreatedAt keeps the first value.
/// A client receiving the merged message ends in the same state as receiving each delta in turn.</para>
/// </summary>
internal static class StreamingMessageMerger
{
    private const string BlockIdKey = "blockId";
    private const string ProducerScopeIdKey = "producerScopeId";
    private const string AgentflowInputKey = "agentflowInput";

    /// <summary>
    /// <para>可以合并的消息：没有消息类型、不是 Result 或 Agentflow 输入快照，内容全部是带正文的文本或推理。</para>
    /// <para>A mergeable message: no message type, not a Result or an Agentflow input snapshot, and every content is text or reasoning with a body.</para>
    /// </summary>
    public static bool CanMerge(AgwMessage message) =>
        !string.IsNullOrEmpty(message.MessageId)
        && message.Contents.Count > 0
        && AgwMessageClassifier.GetMessageType(message) == null
        && !IsTrue(message.AdditionalProperties, AgentflowInputKey)
        && !AgwMessageClassifier.IsResult(message)
        && message.Contents.TrueForAll(static content =>
            content is AgwTextContent { Content: not null } or AgwTextReasoningContent { Content: not null }
        );

    /// <summary>
    /// <para>incoming 可以并入 pending：两者都可合并，且属于同一生产者的同一消息。</para>
    /// <para>Whether incoming can merge into pending: both are mergeable and belong to the same message of the same producer.</para>
    /// </summary>
    public static bool CanMerge(AgwMessage pending, AgwMessage incoming) =>
        CanMerge(incoming)
        && string.Equals(pending.MessageId, incoming.MessageId, StringComparison.Ordinal)
        && pending.Role == incoming.Role
        && string.Equals(pending.Author, incoming.Author, StringComparison.Ordinal)
        && string.Equals(
            ReadString(pending.AdditionalProperties, ProducerScopeIdKey),
            ReadString(incoming.AdditionalProperties, ProducerScopeIdKey),
            StringComparison.Ordinal
        );

    /// <summary>
    /// <para>复制出由合并方持有的消息：内容对象、内容列表与属性字典都是新的实例，之后的合并只修改副本。</para>
    /// <para>Copies a message owned by the merger: content objects, the content list and property dictionaries are new instances, so later merges modify only the copy.</para>
    /// </summary>
    public static AgwMessage Own(AgwMessage message) =>
        message with
        {
            Contents = message.Contents.ConvertAll(CopyContent),
            AdditionalProperties = CopyProperties(message.AdditionalProperties),
        };

    /// <summary>
    /// <para>把 incoming 并入由 Own 得到的 pending，返回合并后的消息；调用方先用 CanMerge(pending, incoming) 判断。</para>
    /// <para>Merges incoming into a pending message obtained from Own and returns the merged message; callers check CanMerge(pending, incoming) first.</para>
    /// </summary>
    public static AgwMessage Merge(AgwMessage pending, AgwMessage incoming)
    {
        foreach (var content in incoming.Contents)
        {
            var blockId = ReadString(content.AdditionalProperties, BlockIdKey);
            var previous =
                blockId == null
                    ? pending.Contents.LastOrDefault()
                    : pending.Contents.Find(item =>
                        string.Equals(
                            ReadString(item.AdditionalProperties, BlockIdKey),
                            blockId,
                            StringComparison.Ordinal
                        )
                    );
            if (
                previous != null
                && string.Equals(
                    ReadString(previous.AdditionalProperties, BlockIdKey),
                    blockId,
                    StringComparison.Ordinal
                )
                && TryAppendText(previous, content)
            )
            {
                if (content.AdditionalProperties != null)
                    previous.AdditionalProperties = Overlay(
                        previous.AdditionalProperties,
                        content.AdditionalProperties
                    );
                continue;
            }

            pending.Contents.Add(CopyContent(content));
        }

        var properties = pending.AdditionalProperties;
        if (incoming.AdditionalProperties != null)
            properties = Overlay(properties, incoming.AdditionalProperties);
        var createdAt = pending.CreatedAt ?? incoming.CreatedAt;
        return ReferenceEquals(properties, pending.AdditionalProperties) && createdAt == pending.CreatedAt
            ? pending
            : pending with
            {
                AdditionalProperties = properties,
                CreatedAt = createdAt,
            };
    }

    private static bool TryAppendText(AgwContent previous, AgwContent incoming)
    {
        switch (previous, incoming)
        {
            case (AgwTextContent { Content: not null } text, AgwTextContent { Content: not null } delta):
                text.Content += delta.Content;
                return true;
            case (
                AgwTextReasoningContent { Content: not null } reasoning,
                AgwTextReasoningContent { Content: not null } delta
            ):
                reasoning.Content += delta.Content;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// CanMerge 只接受文本与推理内容，其他类型在强制转换时失败。
    /// CanMerge accepts only text and reasoning content; any other type fails at the cast.
    /// </summary>
    private static AgwContent CopyContent(AgwContent content) =>
        content is AgwTextReasoningContent reasoning
            ? new AgwTextReasoningContent
            {
                Content = reasoning.Content,
                AdditionalProperties = CopyProperties(reasoning.AdditionalProperties),
            }
            : new AgwTextContent
            {
                Content = ((AgwTextContent)content).Content,
                AdditionalProperties = CopyProperties(content.AdditionalProperties),
            };

    private static AdditionalPropertiesDictionary? CopyProperties(AdditionalPropertiesDictionary? properties) =>
        properties == null ? null : new AdditionalPropertiesDictionary(properties);

    private static AdditionalPropertiesDictionary Overlay(
        AdditionalPropertiesDictionary? target,
        AdditionalPropertiesDictionary source
    )
    {
        target ??= [];
        foreach (var (key, value) in source)
            target[key] = value;
        return target;
    }

    /// <summary>
    /// 与客户端 readString 一致：非空白字符串才算有效值；从事件记录读回的值是 JsonElement。
    /// Matches the client's readString: only a non-blank string counts; values read back from the event log are JsonElements.
    /// </summary>
    private static string? ReadString(AdditionalPropertiesDictionary? properties, string key) =>
        properties?.TryGetValue(key, out var value) == true
            ? value switch
            {
                string text when !string.IsNullOrWhiteSpace(text) => text,
                JsonElement { ValueKind: JsonValueKind.String } element
                    when !string.IsNullOrWhiteSpace(element.GetString()) => element.GetString(),
                _ => null,
            }
            : null;

    private static bool IsTrue(AdditionalPropertiesDictionary? properties, string key) =>
        properties?.TryGetValue(key, out var value) == true
        && value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            _ => false,
        };
}
