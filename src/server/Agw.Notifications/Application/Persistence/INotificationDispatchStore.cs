using Agw.Shared.Data.Entities.Notifications;

namespace Agw.Notifications.Application.Persistence;

public sealed record NotificationDispatchClaim
{
    public required NotificationDispatch Dispatch { get; init; }

    public required Notification Notification { get; init; }

    public required NotificationQuestion? Question { get; init; }

    public required Guid AttemptId { get; init; }
}

public interface INotificationDispatchStore
{
    Task<NotificationDispatchClaim?> TryClaimAsync(Guid dispatchId, string hostId, CancellationToken cancellationToken);

    Task<bool> RenewAsync(Guid dispatchId, Guid attemptId, string hostId, CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        Guid dispatchId,
        Guid attemptId,
        string hostId,
        long sentVersion,
        NotificationDispatchAttemptResult result,
        string? externalMessageId,
        string? error,
        CancellationToken cancellationToken
    );

    Task<int> RecoverDueAsync(CancellationToken cancellationToken);
}
