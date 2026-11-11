using Agw.Events.Configuration;
using Agw.Events.Contracts.Transport;
using Agw.Extensions.Contracts;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Events.Infrastructure.Delivery;

public sealed class EventDeliveryHostedService : BackgroundService
{
    private const string DeliveryTopic = EventTopics.Delivery;
    private static readonly TimeSpan HeldRecoveryInterval = TimeSpan.FromSeconds(5);

    private readonly IEventTransport _transport;
    private readonly IExtensionHostIdentity _hostIdentity;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServerInitializationState _initializationState;
    private readonly EventsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventDeliveryHostedService> _logger;

    public EventDeliveryHostedService(
        IEventTransport transport,
        IExtensionHostIdentity hostIdentity,
        IServiceScopeFactory scopeFactory,
        IServerInitializationState initializationState,
        IOptions<EventsOptions> options,
        TimeProvider timeProvider,
        ILogger<EventDeliveryHostedService> logger
    )
    {
        _transport = transport;
        _hostIdentity = hostIdentity;
        _scopeFactory = scopeFactory;
        _initializationState = initializationState;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_initializationState.IsInitialized)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, stoppingToken).ConfigureAwait(false);
        }

        await Task.WhenAll(ConsumeAsync(stoppingToken), ClaimStaleAsync(stoppingToken), RecoverHeldAsync(stoppingToken))
            .ConfigureAwait(false);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (
                    var delivery in _transport
                        .ConsumeAsync(DeliveryTopic, _hostIdentity.HostId, cancellationToken)
                        .ConfigureAwait(false)
                )
                {
                    await ProcessAsync(delivery, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Event delivery transport consumption failed.");
                await Task.Delay(_options.Outbox.RecoveryInterval, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ClaimStaleAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_options.Outbox.RecoveryInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            try
            {
                await foreach (
                    var delivery in _transport
                        .ClaimStaleAsync(
                            DeliveryTopic,
                            _hostIdentity.HostId,
                            _options.Outbox.StaleAfter,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                )
                {
                    await ProcessAsync(delivery, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Stale event delivery message claim failed.");
            }
        }
    }

    private async Task RecoverHeldAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(HeldRecoveryInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<EventDeliveryProcessor>();
                var count = await processor.RequeueHeldAsync(cancellationToken).ConfigureAwait(false);
                if (count > 0)
                {
                    _logger.LogInformation("Requeued {DeliveryCount} held event deliveries.", count);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Held event delivery recovery failed.");
            }
        }
    }

    private async Task ProcessAsync(TransportDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await scope
                .ServiceProvider.GetRequiredService<EventDeliveryProcessor>()
                .ProcessAsync(delivery, cancellationToken)
                .ConfigureAwait(false);
            await _transport.AckAsync(delivery, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Event delivery processing failed for message {MessageId}.",
                delivery.MessageId
            );
        }
    }
}
