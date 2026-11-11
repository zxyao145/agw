using Agw.Events.Configuration;
using Agw.Events.Contracts.Transport;
using Agw.Extensions.Contracts;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Events.Infrastructure.Dispatch;

public sealed class EventDispatcherHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IEventTransport _transport;
    private readonly IServerInitializationState _initialization;
    private readonly TimeProvider _timeProvider;
    private readonly EventsOptions _options;
    private readonly ILogger<EventDispatcherHostedService> _logger;
    private readonly string _consumer;

    public EventDispatcherHostedService(
        IServiceScopeFactory scopes,
        IEventTransport transport,
        IExtensionHostIdentity hostIdentity,
        IServerInitializationState initialization,
        TimeProvider timeProvider,
        IOptions<EventsOptions> options,
        ILogger<EventDispatcherHostedService> logger
    )
    {
        _scopes = scopes;
        _transport = transport;
        _consumer = hostIdentity.HostId;
        _initialization = initialization;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_initialization.IsInitialized)
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, stoppingToken);
        await Task.WhenAll(ConsumeAsync(stoppingToken), ClaimAsync(stoppingToken));
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (
                    var delivery in _transport.ConsumeAsync(EventTopics.Accepted, _consumer, cancellationToken)
                )
                    await ProcessAsync(delivery, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Event dispatcher transport consumption failed");
                await Task.Delay(_options.Outbox.RecoveryInterval, _timeProvider, cancellationToken);
            }
        }
    }

    private async Task ClaimAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_options.Outbox.RecoveryInterval, _timeProvider, cancellationToken);
            try
            {
                await foreach (
                    var delivery in _transport.ClaimStaleAsync(
                        EventTopics.Accepted,
                        _consumer,
                        _options.Outbox.StaleAfter,
                        cancellationToken
                    )
                )
                    await ProcessAsync(delivery, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Event dispatcher pending message claim failed");
            }
        }
    }

    private async Task ProcessAsync(TransportDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope
                .ServiceProvider.GetRequiredService<EventDispatcher>()
                .DispatchAsync(delivery, cancellationToken);
            await _transport.AckAsync(delivery, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Event dispatch failed for message {MessageId}", delivery.MessageId);
        }
    }
}
