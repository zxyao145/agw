using Microsoft.Extensions.AI;

namespace Agw.Agents.Contracts.Messages;

/// <summary>
/// 消息分类的唯一判定。Result 的统一形态是顶层 type = result、resultFormat、resultSourceMessageId 与 purpose = result；
/// 内容上的 type = result 是 SDK 与旧数据的标记，同样判定为 Result。
/// The single message classification. The unified Result shape is top-level type = result, resultFormat, resultSourceMessageId and purpose = result;
/// a content-level type = result is a marker from SDKs and older data and is classified as a Result as well.
/// </summary>
public static class AgwMessageClassifier
{
    public const string TypeKey = "type";
    public const string StatusKey = "status";
    public const string ResultType = AgwMessageTypes.Result;
    public const string ResultFormatKey = "resultFormat";
    public const string ResultSourceMessageIdKey = "resultSourceMessageId";

    public static string? GetMessageType(AgwMessage message) => GetMessageType(message.AdditionalProperties);

    public static string? GetMessageType(AdditionalPropertiesDictionary? properties) =>
        properties?.TryGetValue(TypeKey, out var value) == true ? value?.ToString() : null;

    public static bool IsResult(ChatMessage message) => IsResult(message.AdditionalProperties, message.Contents);

    public static bool IsResult(AgwMessage message) =>
        HasType(message.AdditionalProperties, ResultType)
        || message.Contents.Any(content => HasType(content.AdditionalProperties, ResultType));

    public static bool IsResult(AdditionalPropertiesDictionary? properties, IEnumerable<AIContent> contents) =>
        HasType(properties, ResultType) || contents.Any(content => HasType(content.AdditionalProperties, ResultType));

    /// <summary>
    /// 控制消息描述 Turn 的生命周期与等待状态，非流式与只输出结果的连接也即时收到它们。
    /// Control messages describe the turn lifecycle and wait states; non-streaming and result-only connections receive them immediately too.
    /// </summary>
    public static bool IsControl(string? type) =>
        type
            is AgwMessageTypes.TurnStart
                or AgwMessageTypes.TurnFinished
                or AgwMessageTypes.StepDiscarded
                or AgwMessageTypes.InteractionRequest
                or AgwMessageTypes.AgentflowCheckpoint
        || type?.StartsWith(AgwMessageTypes.HumanGatePrefix, StringComparison.Ordinal) == true
        || type?.StartsWith(AgwMessageTypes.ToolApprovalPrefix, StringComparison.Ordinal) == true;

    public static bool IsControl(AgwMessage message) => IsControl(GetMessageType(message));

    public static bool IsControl(ChatMessage message) => IsControl(GetMessageType(message.AdditionalProperties));

    public static bool IsTurnStart(AgwMessage message) =>
        string.Equals(GetMessageType(message), AgwMessageTypes.TurnStart, StringComparison.Ordinal);

    public static bool IsTurnFinished(AgwMessage message) =>
        string.Equals(GetMessageType(message), AgwMessageTypes.TurnFinished, StringComparison.Ordinal);

    public static bool TryGetTurnFinishedStatus(AgwMessage message, out string? status)
    {
        status = null;
        if (!IsTurnFinished(message))
            return false;
        // 从事件记录读回的消息中属性值是 JsonElement，按文本读取。
        // Properties of messages read back from the event log are JsonElements and are read as text.
        status = message.AdditionalProperties?.TryGetValue(StatusKey, out var value) == true ? value?.ToString() : null;
        return true;
    }

    public static bool IsInteractionRequest(AgwMessage message) =>
        string.Equals(GetMessageType(message), AgwMessageTypes.InteractionRequest, StringComparison.Ordinal);

    public static bool IsToolMessage(string? type) =>
        type
            is AgwMessageTypes.ToolTodoSnapshot
                or AgwMessageTypes.ToolModeStatus
                or AgwMessageTypes.ToolBackgroundTaskStatus
                or AgwMessageTypes.ToolWarning;

    private static bool HasType(AdditionalPropertiesDictionary? properties, string expected) =>
        string.Equals(GetMessageType(properties), expected, StringComparison.Ordinal);
}
