namespace Agw.Notifications.Api.Dtos;

public sealed record NotificationAnswerRequest(Guid NotificationId, int? Option = null, string? Text = null);

public sealed record NotificationReadRequest(Guid NotificationId);

public sealed record NotificationDeliveredRequest(Guid NotificationId, long Version);

public sealed record NotificationDispatchRetryRequest(Guid DispatchId);
