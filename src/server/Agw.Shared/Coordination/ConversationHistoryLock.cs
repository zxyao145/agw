namespace Agw.Shared.Coordination;

/// <summary>
/// 对话历史写入锁：分配序号与改写历史的操作都在项目生命周期锁之后取得它。
/// The conversation history write lock: sequence allocation and history rewrites acquire it after the project lifecycle lock.
/// </summary>
public static class ConversationHistoryLock
{
    public static string GetResourceName(Guid projectId, string normalizedContextId) =>
        $"conversation-history:{projectId:D}:{normalizedContextId}";
}
