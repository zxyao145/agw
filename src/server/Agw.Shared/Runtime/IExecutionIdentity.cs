namespace Agw.Shared.Runtime;

/// <summary>
/// 一次执行对 Shared 层公开的可信身份与快照。
/// The trusted identity and snapshots of one execution, as exposed to the Shared layer.
/// </summary>
public interface IExecutionIdentity
{
    string UserId { get; }

    Guid ProjectId { get; }

    Guid ProjectConversationId { get; }

    string ContextId { get; }

    int Generation { get; }

    ProjectWorkspaceSnapshot WorkspaceSnapshot { get; }
}
