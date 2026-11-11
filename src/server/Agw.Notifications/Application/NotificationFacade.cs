using Agw.Notifications.Contracts;

namespace Agw.Notifications.Application;

public sealed class NotificationFacade : INotificationFacade
{
    private readonly NotificationAppService _notificationAppService;

    public NotificationFacade(NotificationAppService notificationAppService)
    {
        _notificationAppService = notificationAppService;
    }

    public Task<Guid> CreateAsync(NotificationRequest request, CancellationToken cancellationToken = default) =>
        _notificationAppService.CreateAsync(request, cancellationToken);

    public Task UpdateAsync(NotificationUpdate update, CancellationToken cancellationToken = default) =>
        _notificationAppService.UpdateAsync(update, cancellationToken);
}

public sealed class NotificationAnswerFacade : INotificationAnswerFacade
{
    private readonly NotificationAppService _notificationAppService;

    public NotificationAnswerFacade(NotificationAppService notificationAppService)
    {
        _notificationAppService = notificationAppService;
    }

    public Task<NotificationAnswerResult> SubmitAsync(
        NotificationAnswer answer,
        CancellationToken cancellationToken = default
    ) => _notificationAppService.SubmitAnswerAsync(answer, cancellationToken);
}

public sealed class NotificationStagingFacade : INotificationStaging
{
    private readonly NotificationAppService _notificationAppService;

    public NotificationStagingFacade(NotificationAppService notificationAppService)
    {
        _notificationAppService = notificationAppService;
    }

    public Task<Guid> StageAsync(NotificationRequest request, CancellationToken cancellationToken = default) =>
        _notificationAppService.StageAsync(request, cancellationToken);
}
