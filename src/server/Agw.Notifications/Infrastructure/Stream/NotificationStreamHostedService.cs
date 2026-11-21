using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Notifications.Application;
using Agw.Notifications.Application.Persistence;
using Agw.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Notifications.Infrastructure.Stream;

public sealed class NotificationStreamHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly NotificationStreamHub _streamHub;
    private readonly TimeProvider _timeProvider;
    private readonly NotificationStreamOptions _options;
    private readonly ILogger<NotificationStreamHostedService> _logger;

    public NotificationStreamHostedService(
        IServiceScopeFactory serviceScopeFactory,
        NotificationStreamHub streamHub,
        TimeProvider timeProvider,
        IOptions<NotificationStreamOptions> options,
        ILogger<NotificationStreamHostedService> logger
    )
    {
        _serviceScopeFactory = serviceScopeFactory;
        _streamHub = streamHub;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batches = _streamHub.GetPollBatches();
                foreach (var batch in batches)
                {
                    await using var scope = _serviceScopeFactory.CreateAsyncScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<INotificationsDbContext>();
                    using var userScope = UserInfoUtil.Push(
                        new ClaimsPrincipal(
                            new ClaimsIdentity(
                                [new Claim(ClaimTypes.NameIdentifier, batch.UserId)],
                                "notification-stream"
                            )
                        )
                    );
                    var notifications = await dbContext
                        .Notifications.AsNoTracking()
                        .Where(notification =>
                            notification.CreateBy == batch.UserId && notification.ChangedAt >= batch.From
                        )
                        .OrderBy(notification => notification.ChangedAt)
                        .ThenBy(notification => notification.Id)
                        .ToListAsync(stoppingToken)
                        .ConfigureAwait(false);
                    foreach (var notification in notifications)
                    {
                        _streamHub.Publish(
                            batch.UserId,
                            new NotificationStreamItem
                            {
                                Id = notification.Id,
                                CreatedAt = notification.CreateTime,
                                Kind = (NotificationKind)notification.Kind,
                                State = (NotificationState)notification.State,
                                Title = notification.Title,
                                Text = notification.Text,
                                Link = notification.Link,
                                ReadAt = notification.ReadAt,
                                ChangedAt = notification.ChangedAt,
                                Version = notification.Version,
                            }
                        );
                    }
                    _streamHub.AdvanceWatermark(batch.UserId, _timeProvider.GetUtcNow());
                }

                if (batches.Count == 0)
                    _logger.LogDebug("Notification stream poll has no active subscribers.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Notification stream poll failed.");
            }

            await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
