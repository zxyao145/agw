namespace Agw.Projects.Contracts.Execution;

public enum ProjectTaskStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Canceled = 4,
}

public sealed record ProjectTaskSnapshot(
    Guid TaskId,
    Guid ProjectConversationId,
    Guid ProjectId,
    string ContextId,
    Guid? JobId,
    string Title,
    ProjectTaskStatus Status,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? FinishedAt,
    int Generation = 0
);

public sealed record ResolveProjectTaskRequest(
    Guid? TaskId,
    Guid ConversationId,
    Guid? ProjectId,
    string Input,
    bool Resume,
    string OwnerUserId
);

public sealed record StartProjectTaskRequest(
    Guid ProjectId,
    Guid TaskId,
    Guid? JobId,
    string Input,
    string? Title,
    string? ContextId,
    string OwnerUserId,
    ProjectTaskStatus InitialStatus
);

public sealed record FinishProjectTaskRequest(
    Guid TaskId,
    ProjectTaskStatus Status,
    string? ErrorMessage,
    string OwnerUserId
);

public interface IProjectTaskFacade
{
    Task<int?> GetGenerationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按项目与对话读取当前用户已保存对话的执行上下文；对话不存在或属于其他用户时返回 null。
    /// Reads the execution context of the current user's saved conversation by project and conversation; returns null when the conversation is missing or belongs to another user.
    /// </summary>
    Task<string?> FindContextIdAsync(
        Guid projectId,
        Guid conversationId,
        CancellationToken cancellationToken = default
    );

    Task<ProjectTaskSnapshot> ResolveAsync(
        ResolveProjectTaskRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ProjectTaskSnapshot?> GetAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<ProjectTaskSnapshot> GetOrCreateAsync(
        StartProjectTaskRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ProjectTaskSnapshot?> FinishAsync(
        FinishProjectTaskRequest request,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyDictionary<Guid, Guid>> ResolveConversationIdsAsync(
        IReadOnlyCollection<Guid> taskIds,
        CancellationToken cancellationToken = default
    );
}
