using System.Text.Json;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.TaskPilot;

internal static class TaskPilotNotificationService
{
    public static Guid StageAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotCard card,
        NotificationKind kind,
        IReadOnlyList<NotificationOption> options,
        int? recommend,
        bool allowText,
        IReadOnlyList<string> channelKeys,
        string correlationKey,
        Guid? parentEventId
    ) =>
        StageAsync(
            dbContext,
            context.JsonOptions,
            card,
            kind,
            options,
            recommend,
            allowText,
            channelKeys,
            correlationKey,
            parentEventId
        );

    public static Guid StageAsync(
        TaskPilotDbContext dbContext,
        JsonSerializerOptions jsonOptions,
        TaskPilotCard card,
        NotificationKind kind,
        IReadOnlyList<NotificationOption> options,
        int? recommend,
        bool allowText,
        IReadOnlyList<string> channelKeys,
        string correlationKey,
        Guid? parentEventId
    )
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(channelKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationKey);

        if (dbContext.Entry(card).State == EntityState.Detached)
        {
            dbContext.Cards.Add(card);
        }

        card.OptionsJson = JsonSerializer.Serialize(options, jsonOptions);
        card.Recommend = recommend;
        card.AllowText = allowText;
        card.NotificationVersion = 0;
        var request = new NotificationRequest
        {
            IdempotencyKey = IdempotencyKey(card.Id, 1),
            Kind = kind,
            Title = card.Title,
            Text = card.Text,
            Options = options,
            Recommend = recommend,
            AllowText = allowText,
            ChannelKeys = channelKeys,
            CorrelationKey = correlationKey,
            ParentEventId = parentEventId,
        };
        Guid outboxId = ExtensionOutbox.Enqueue(
            dbContext,
            ExtensionOutboxKind.Notify,
            request.IdempotencyKey,
            request,
            jsonOptions
        );
        card.NotificationOutboxId = outboxId;
        return outboxId;
    }

    public static string IdempotencyKey(Guid cardId, long version) => $"taskpilot:card:{cardId:D}:v{version}";

    public static string UpdateIdempotencyKey(Guid cardId, long desiredVersion) =>
        $"taskpilot:card:{cardId:D}:v{desiredVersion}:update";

    public static Guid? StageUpdateAsync(
        TaskPilotDbContext dbContext,
        JsonSerializerOptions jsonOptions,
        TaskPilotCard card
    )
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(card);
        if (card.NotificationId is not { } notificationId)
        {
            return null;
        }

        long expectedVersion = Math.Max(1, card.NotificationVersion);
        var update = new NotificationUpdate
        {
            NotificationId = notificationId,
            Version = expectedVersion,
            Title = card.Title,
            Text = card.Text,
            State = card.State switch
            {
                TaskPilotCardState.Answered => NotificationState.Answered,
                TaskPilotCardState.Resolved => NotificationState.Resolved,
                TaskPilotCardState.Closed => NotificationState.Closed,
                _ => NotificationState.Open,
            },
        };
        Guid outboxId = ExtensionOutbox.Enqueue(
            dbContext,
            ExtensionOutboxKind.UpdateNotification,
            UpdateIdempotencyKey(card.Id, expectedVersion + 1),
            update,
            jsonOptions
        );
        card.NotificationUpdateOutboxId = outboxId;
        card.NotificationUpdateExpectedVersion = expectedVersion;
        card.Version++;
        return outboxId;
    }
}
