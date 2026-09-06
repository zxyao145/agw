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
    string? ContextId,
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

    Task<IReadOnlyDictionary<Guid, string?>> ResolveContextIdsAsync(
        IReadOnlyCollection<Guid> taskIds,
        CancellationToken cancellationToken = default
    );
}
