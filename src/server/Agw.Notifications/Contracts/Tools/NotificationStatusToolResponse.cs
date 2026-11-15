using System.Text.Json.Serialization;

namespace Agw.Notifications.Contracts.Tools;

internal sealed record NotificationStatusToolResponse(
    [property: JsonPropertyName("notificationId")] Guid NotificationId,
    [property: JsonPropertyName("dispatches")] IReadOnlyList<NotificationDispatchStatusToolResponse> Dispatches
);

internal sealed record NotificationDispatchStatusToolResponse(
    [property: JsonPropertyName("channelKey")] string ChannelKey,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("externalMessageId")] string? ExternalMessageId,
    [property: JsonPropertyName("nextAttemptAt")] DateTimeOffset? NextAttemptAt,
    [property: JsonPropertyName("lastError")] string? LastError
);
