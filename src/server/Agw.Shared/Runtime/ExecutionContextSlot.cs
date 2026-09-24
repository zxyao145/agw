namespace Agw.Shared.Runtime;

/// <summary>
/// Agw 执行数据在异步调用链中的唯一槽位；只有 Execution 的执行作用域写入它。
/// The single ambient slot for Agw execution data; only the Execution scope writes to it.
/// </summary>
public static class ExecutionContextSlot
{
    private static readonly AsyncLocal<IExecutionIdentity?> Slot = new();

    public static IExecutionIdentity? Current => Slot.Value;

    /// <summary>
    /// 返回属于指定项目的工作目录快照；槽位为空或项目不同时返回 null。
    /// Returns the workspace snapshot of the given project, or null when the slot is empty or belongs to another project.
    /// </summary>
    public static ProjectWorkspaceSnapshot? GetWorkspaceSnapshot(Guid projectId) =>
        Slot.Value is { } identity && identity.ProjectId == projectId ? identity.WorkspaceSnapshot : null;

    /// <summary>
    /// 返回绑定到指定项目与对话的当前执行身份；槽位为空或属于其他对话时返回 null。
    /// Returns the current execution identity bound to the given project and conversation, or null when the slot is empty or belongs to another conversation.
    /// </summary>
    public static IExecutionIdentity? FindBound(Guid projectId, string contextId) =>
        Slot.Value is { } identity
        && identity.ProjectId == projectId
        && string.Equals(identity.ContextId, contextId, StringComparison.OrdinalIgnoreCase)
            ? identity
            : null;

    public static IDisposable Push(IExecutionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var previous = Slot.Value;
        Slot.Value = identity;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly IExecutionIdentity? _previous;
        private int _disposed;

        public Scope(IExecutionIdentity? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Slot.Value = _previous;
            }
        }
    }
}
