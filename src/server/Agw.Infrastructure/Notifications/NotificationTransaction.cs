using System.Text.Json;
using Agw.Events.Contracts;
using Agw.Infrastructure.Data;
using Agw.Notifications.Application.Persistence;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Notifications;

public sealed class NotificationTransaction : INotificationTransaction
{
    private const string DispatchTopic = "agw.notifications.dispatch";
    private readonly AgwDbContext _dbContext;
    private readonly IEventStaging _eventStaging;
    private readonly IOutboxStaging _outboxStaging;

    public NotificationTransaction(AgwDbContext dbContext, IEventStaging eventStaging, IOutboxStaging outboxStaging)
    {
        _dbContext = dbContext;
        _eventStaging = eventStaging;
        _outboxStaging = outboxStaging;
    }

    public async Task<NotificationCreateResult> CreateAsync(
        Notification notification,
        NotificationQuestion? question,
        IReadOnlyList<NotificationDispatch> dispatches,
        CancellationToken cancellationToken
    )
    {
        var result = await StageCreateAsync(notification, question, dispatches, cancellationToken)
            .ConfigureAwait(false);
        if (result.Duplicate)
            return result;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (DbUpdateException)
        {
            var existing = await FindDuplicateAsync(notification, cancellationToken).ConfigureAwait(false);
            if (existing is null)
                throw;

            return new NotificationCreateResult(existing.Id, Duplicate: true);
        }
    }

    public async Task<NotificationCreateResult> StageCreateAsync(
        Notification notification,
        NotificationQuestion? question,
        IReadOnlyList<NotificationDispatch> dispatches,
        CancellationToken cancellationToken
    )
    {
        var existing = await FindDuplicateAsync(notification, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return new NotificationCreateResult(existing.Id, Duplicate: true);

        _dbContext.Notifications.Add(notification);
        if (question is not null)
            _dbContext.NotificationQuestions.Add(question);
        foreach (var dispatch in dispatches)
        {
            _dbContext.NotificationDispatches.Add(dispatch);
            StageDispatch(dispatch);
        }
        return new NotificationCreateResult(notification.Id, Duplicate: false);
    }

    public void StageDispatch(NotificationDispatch dispatch)
    {
        var json = JsonSerializer.SerializeToElement(
            new { dispatchId = dispatch.Id, messageVersion = dispatch.MessageVersion }
        );
        var message = new CloudEventEnvelope
        {
            Id = $"{dispatch.Id:D}:{dispatch.MessageVersion}",
            Source = "/agw/notifications",
            Type = "agw.notification.dispatch",
            DataContentType = "application/json",
            Data = new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = json.GetRawText(),
                Json = json,
            },
        };
        _outboxStaging.Stage(
            new OutboxMessage
            {
                Topic = DispatchTopic,
                EntityId = dispatch.Id,
                Message = message,
            }
        );
    }

    public Task<EventPublishResult> StageEventAsync(EventStagingRequest request, CancellationToken cancellationToken) =>
        _eventStaging.StageAsync(request, cancellationToken);

    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        _dbContext.ExecuteAndSaveAsync(action, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _dbContext.SaveChangesAsync(cancellationToken);

    private Task<Notification?> FindDuplicateAsync(Notification notification, CancellationToken cancellationToken) =>
        _dbContext
            .Notifications.AsNoTracking()
            .SingleOrDefaultAsync(
                existing =>
                    existing.CreateBy == notification.CreateBy
                    && existing.CallerKey == notification.CallerKey
                    && existing.IdempotencyKey == notification.IdempotencyKey,
                cancellationToken
            );
}
