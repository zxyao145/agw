using Microsoft.Extensions.AI;

namespace Agw.Shared.Contracts.Projects;

/// <summary>
/// Internal message metadata used to keep display-only history out of model context.
/// </summary>
public static class ConversationHistoryMetadata
{
    public const string ModelHistoryExcludedKey = "modelHistoryExcluded";

    /// <summary>
    /// 判断消息是否应从后续模型历史和跨 Agent 交接中排除。
    /// </summary>
    /// <param name="message">要检查的聊天消息。</param>
    /// <returns>需要排除时返回 <see langword="true" />；否则返回 <see langword="false" />。</returns>
    public static bool IsModelHistoryExcluded(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.AdditionalProperties?.TryGetValue(ModelHistoryExcludedKey, out var value) == true
            && string.Equals(value?.ToString(), bool.TrueString, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 将消息标记为仅用于历史展示，不参与后续模型上下文或跨 Agent 交接。
    /// </summary>
    /// <param name="message">要标记的聊天消息。</param>
    public static void ExcludeFromModelHistory(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.AdditionalProperties ??= [];
        message.AdditionalProperties[ModelHistoryExcludedKey] = true;
    }
}
