using Agw.Events.Contracts;
using Agw.Shared.Data.Entities.Notifications;

namespace Agw.Notifications.Application.Persistence;

public sealed record NotificationCreateResult(Guid NotificationId, bool Duplicate);

public interface INotificationTransaction
{
    Task<NotificationCreateResult> StageCreateAsync(
        Notification notification,
        NotificationQuestion? question,
        IReadOnlyList<NotificationDispatch> dispatches,
        CancellationToken cancellationToken
    );

    Task<NotificationCreateResult> CreateAsync(
        Notification notification,
        NotificationQuestion? question,
        IReadOnlyList<NotificationDispatch> dispatches,
        CancellationToken cancellationToken
    );

    void StageDispatch(NotificationDispatch dispatch);

    Task<EventPublishResult> StageEventAsync(EventStagingRequest request, CancellationToken cancellationToken);

    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
