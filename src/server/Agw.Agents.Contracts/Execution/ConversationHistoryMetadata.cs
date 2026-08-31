using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Contracts.Execution;

/// <summary>
/// Internal message metadata used to keep display-only history out of model context.
/// </summary>
public static class ConversationHistoryMetadata
{
    public const string ModelHistoryExcludedKey = "modelHistoryExcluded";
    public const string PersistenceExcludedKey = "persistenceExcluded";
    public const string UserMemorySourceId = "Agw.UserMemory";
    public const string LegacyUserMemorySourceId = "Agw.Tools.ToolBlocks.Blocks.UserMemory.UserMemoryProvider";

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

    public static bool IsPersistenceExcluded(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.AdditionalProperties?.TryGetValue(PersistenceExcludedKey, out var value) == true
            && string.Equals(value?.ToString(), bool.TrueString, StringComparison.OrdinalIgnoreCase);
    }

    public static void ExcludeFromPersistence(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.AdditionalProperties ??= [];
        message.AdditionalProperties[PersistenceExcludedKey] = true;
    }

    public static bool IsUserMemoryContext(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var sourceId = GetSourceId(message);
        return string.Equals(sourceId, UserMemorySourceId, StringComparison.Ordinal)
            || string.Equals(sourceId, LegacyUserMemorySourceId, StringComparison.Ordinal);
    }

    private static string? GetSourceId(ChatMessage message)
    {
        var sourceId = message.GetAgentRequestMessageSourceId();
        if (sourceId != null)
        {
            return sourceId;
        }

        if (
            message.AdditionalProperties?.TryGetValue(
                AgentRequestMessageSourceAttribution.AdditionalPropertiesKey,
                out var attribution
            ) != true
            || attribution is not JsonElement { ValueKind: JsonValueKind.Object } element
        )
        {
            return null;
        }

        return element.TryGetProperty("sourceId", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
