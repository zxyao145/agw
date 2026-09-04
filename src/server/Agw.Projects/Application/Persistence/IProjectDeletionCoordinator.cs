namespace Agw.Projects.Application.Persistence;

public interface IProjectDeletionCoordinator
{
    Task<bool> ClearConversationRecordsAsync(
        ProjectConversationDeletionTarget target,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteConversationAsync(
        ProjectConversationDeletionTarget target,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteAllConversationsAsync(ProjectDeletionTarget target, CancellationToken cancellationToken = default);

    Task<bool> DeleteProjectAsync(ProjectDeletionTarget target, CancellationToken cancellationToken = default);
}

public sealed record ProjectDeletionTarget(Guid ProjectId, string OwnerUserId);

public sealed record ProjectConversationDeletionTarget(
    Guid ProjectId,
    Guid ConversationId,
    string ContextId,
    string OwnerUserId
);
