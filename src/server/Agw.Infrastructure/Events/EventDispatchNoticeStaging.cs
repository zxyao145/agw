using Agw.Events.Application.Persistence;
using Agw.Notifications.Contracts;
using Agw.Shared.Data.Entities.Events;

namespace Agw.Infrastructure.Events;

public sealed class EventDispatchNoticeStaging : IEventDispatchNoticeStaging
{
    private readonly INotificationStaging _notifications;

    public EventDispatchNoticeStaging(INotificationStaging notifications)
    {
        _notifications = notifications;
    }

    public async Task StageHopLimitAsync(EventRecord record, CancellationToken cancellationToken)
    {
        await _notifications.StageAsync(
            new NotificationRequest
            {
                IdempotencyKey = $"event:{record.Id}:hop-limit",
                Kind = NotificationKind.Notice,
                Title = "事件处理已停止",
                Text = $"事件 {record.CeId} 的处理链深度超过配置上限。",
                Link = $"/events?eventId={record.Id}",
                CorrelationKey = $"event:{record.Id}",
                ParentEventId = record.Id,
            },
            cancellationToken
        );
    }
}
