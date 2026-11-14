using Agw.Shared.Contracts.Events;

namespace Agw.Shared.Contracts.Conversations;

/// <summary>
/// 定义原生会话和事件投递会话的类型。
/// Defines the kinds of native and event delivery conversations.
/// </summary>
public static class ConversationKinds
{
    public const string Native = "native";
    public const string EventPrefix = "event:";

    private static readonly HashSet<string> EventKinds = Enum.GetValues<EventSourceKind>()
        .Select(ForEventSource)
        .ToHashSet(StringComparer.Ordinal);

    public static string ForEventSource(EventSourceKind kind) => EventPrefix + kind.ToString().ToLowerInvariant();

    public static bool IsEvent(string kind) => EventKinds.Contains(kind);
}
