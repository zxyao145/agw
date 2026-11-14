namespace Agw.Shared.Contracts.Events;

/// <summary>
/// 定义 Event Source 和订阅的管理方取值，管理方内部的标识保存在 managed_key。
/// Defines who manages an event source or subscription; the manager's own identifier is stored in managed_key.
/// </summary>
public static class EventManagedBy
{
    public const string User = "user";
    public const string Job = "job";
    public const string ExtensionPrefix = "ext:";

    public static string ForExtension(Guid extensionId) => $"{ExtensionPrefix}{extensionId:D}";

    public static bool IsExtension(string managedBy) => managedBy.StartsWith(ExtensionPrefix, StringComparison.Ordinal);
}
