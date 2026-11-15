using System.ComponentModel;
using Agw.Notifications.Contracts;
using Agw.Notifications.Contracts.Tools;
using Agw.Shared.Exceptions;
using Agw.Tools.Abstractions;
using Agw.Tools.Abstractions.Attributes;

namespace Agw.Notifications.Application.Tools;

internal sealed class NotificationToolExecutor
{
    private readonly NotificationAppService _notifications;

    public NotificationToolExecutor(NotificationAppService notifications)
    {
        _notifications = notifications;
    }

    [AgwTool(
        "list_notification_channels",
        AgwToolPermission.ReadOnly,
        Category = "Notifications",
        AllowInPlanMode = true
    )]
    [Description("Lists the current user's available notification destinations and their channel keys.")]
    public Task<IReadOnlyList<NotificationChannelResponse>> ListChannelsAsync(CancellationToken cancellationToken) =>
        _notifications.ListChannelsAsync(cancellationToken);

    [AgwTool("send_notification", AgwToolPermission.Write, Category = "Notifications")]
    [Description("Queues a notice for the selected channels and returns its notification ID.")]
    public Task<Guid> SendAsync(
        [Description("Notification title, at most 200 characters.")] string title,
        [Description("Notification text, at most 4000 characters.")] string text,
        [Description("One or more distinct channel keys returned by list_notification_channels.")] string[] channelKeys,
        CancellationToken cancellationToken
    )
    {
        if (channelKeys is not { Length: > 0 })
            throw new AgwException(ErrorCodes.InvalidParam, "Select at least one notification channel.");

        var requestKey = Guid.CreateVersion7().ToString("N");
        return _notifications.CreateAsync(
            new NotificationRequest
            {
                IdempotencyKey = requestKey,
                CorrelationKey = requestKey,
                Kind = NotificationKind.Notice,
                Title = title,
                Text = text,
                ChannelKeys = channelKeys,
            },
            cancellationToken
        );
    }

    [AgwTool("get_notification_status", AgwToolPermission.ReadOnly, Category = "Notifications", AllowInPlanMode = true)]
    [Description("Checks delivery for each channel of a notification. Sent means the channel reported success.")]
    public async Task<NotificationStatusToolResponse> GetStatusAsync(
        [Description("The notification ID returned by send_notification.")] Guid notificationId,
        CancellationToken cancellationToken
    )
    {
        var notification =
            await _notifications.GetAsync(notificationId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.NotificationNotFound);

        return new NotificationStatusToolResponse(
            notification.Id,
            notification
                .Dispatches.Select(static dispatch => new NotificationDispatchStatusToolResponse(
                    dispatch.ChannelKey,
                    dispatch.State.ToString(),
                    dispatch.ExternalMessageId,
                    dispatch.NextAttemptAt,
                    dispatch.LastError
                ))
                .ToArray()
        );
    }
}
