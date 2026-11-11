using Agw.Events.Contracts.Transport;
using Agw.Extensions.Contracts;
using Agw.Notifications.Application;
using Agw.Notifications.Configuration;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Notifications.Infrastructure.Dispatch;

public sealed class NotificationDispatchHostedService : BackgroundService
{
    private const string DispatchTopic = "agw.notifications.dispatch";
    private readonly IEventTransport _eventTransport;
    private readonly IExtensionHostIdentity _hostIdentity;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IServerInitializationState _initialization;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NotificationDispatchHostedService> _logger;
    private readonly NotificationDispatchOptions _options;
    private readonly TimeSpan _claimDuration;

    public NotificationDispatchHostedService(
        IEventTransport eventTransport,
        IExtensionHostIdentity hostIdentity,
        IServiceScopeFactory serviceScopeFactory,
        IServerInitializationState initialization,
        TimeProvider timeProvider,
        IOptions<NotificationDispatchOptions> options,
        ILogger<NotificationDispatchHostedService> logger
    )
    {
        _eventTransport = eventTransport;
        _hostIdentity = hostIdentity;
        _serviceScopeFactory = serviceScopeFactory;
        _initialization = initialization;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.Value;
        _claimDuration = _options.ClaimDuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_initialization.IsInitialized)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, stoppingToken).ConfigureAwait(false);
        }

        await Task.WhenAll(ConsumeAsync(stoppingToken), ClaimStaleAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (
                    var delivery in _eventTransport
                        .ConsumeAsync(DispatchTopic, _hostIdentity.HostId, cancellationToken)
                        .ConfigureAwait(false)
                )
                    await ProcessAsync(delivery, cancellationToken).ConfigureAwait(false);

                if (!cancellationToken.IsCancellationRequested)
                    await Task.Delay(_options.RecoveryInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Notification dispatch transport consumption failed.");
                await Task.Delay(_options.RecoveryInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ClaimStaleAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_options.RecoveryInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            try
            {
                await foreach (
                    var delivery in _eventTransport
                        .ClaimStaleAsync(DispatchTopic, _hostIdentity.HostId, _claimDuration, cancellationToken)
                        .ConfigureAwait(false)
                )
                    await ProcessAsync(delivery, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Stale notification dispatch claim failed.");
            }
        }
    }

    private async Task ProcessAsync(TransportDelivery delivery, CancellationToken cancellationToken)
    {
        if (!TryReadDispatch(delivery.Message, out var dispatchId, out var messageVersion))
        {
            _logger.LogWarning(
                "Ignoring malformed notification dispatch message {MessageId} from {Topic}.",
                delivery.Message.Id,
                delivery.Topic
            );
            await _eventTransport.AckAsync(delivery, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var dispatchService = scope.ServiceProvider.GetRequiredService<NotificationDispatchAppService>();
            await dispatchService.DispatchAsync(dispatchId, cancellationToken).ConfigureAwait(false);
            await _eventTransport.AckAsync(delivery, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            _logger.LogDebug(
                exception,
                "Notification dispatch {DispatchId} changed during delivery of version {MessageVersion}.",
                dispatchId,
                messageVersion
            );
            await _eventTransport.AckAsync(delivery, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Notification dispatch {DispatchId} failed before recording its outcome.",
                dispatchId
            );
        }
    }

    private static bool TryReadDispatch(CloudEventEnvelope message, out Guid dispatchId, out long messageVersion)
    {
        dispatchId = default;
        messageVersion = default;
        if (
            message.Type != "agw.notification.dispatch"
            || message.Source != "/agw/notifications"
            || message.Data is not { Kind: CloudEventDataKind.Json, Json: { } data }
        )
            return false;
        if (
            !data.TryGetProperty("dispatchId", out var dispatchIdProperty)
            || !dispatchIdProperty.TryGetGuid(out dispatchId)
            || !data.TryGetProperty("messageVersion", out var messageVersionProperty)
            || !messageVersionProperty.TryGetInt64(out messageVersion)
            || messageVersion < 1
        )
            return false;
        return message.Id == $"{dispatchId:D}:{messageVersion}";
    }
}
