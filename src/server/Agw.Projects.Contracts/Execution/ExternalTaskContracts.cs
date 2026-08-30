using System.Text.Json;

namespace Agw.Projects.Contracts.Execution;

public sealed record ExternalTaskSnapshot(ProjectTaskSnapshot Task, JsonElement? Payload);

public sealed record SaveExternalTaskSnapshotRequest(
    Guid ProjectId,
    Guid TaskId,
    string ContextId,
    string Title,
    ProjectTaskStatus Status,
    string? ErrorMessage,
    DateTimeOffset StatusTimestamp,
    string? AgentName,
    JsonElement Payload
);

public enum ExternalTaskSaveResult
{
    Saved = 0,
    TaskIdConflict = 1,
}

public interface IExternalTaskSnapshotStore
{
    Task<ExternalTaskSnapshot?> GetAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExternalTaskSnapshot>> ListAsync(
        Guid projectId,
        string? contextId,
        CancellationToken cancellationToken = default
    );

    Task<ExternalTaskSaveResult> SaveAsync(
        SaveExternalTaskSnapshotRequest request,
        CancellationToken cancellationToken = default
    );

    Task DeleteAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default);
}
