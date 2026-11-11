using Agw.Events.Configuration;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Events;

public sealed class EventRecoveryHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IServerInitializationState _initialization;
    private readonly TimeProvider _timeProvider;
    private readonly EventsOptions _options;
    private readonly ILogger<EventRecoveryHostedService> _logger;

    public EventRecoveryHostedService(
        IServiceScopeFactory scopes,
        IServerInitializationState initialization,
        TimeProvider timeProvider,
        IOptions<EventsOptions> options,
        ILogger<EventRecoveryHostedService> logger
    )
    {
        _scopes = scopes;
        _initialization = initialization;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startup = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_initialization.IsInitialized)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    await scope
                        .ServiceProvider.GetRequiredService<EventRecordRecovery>()
                        .RecoverAsync(startup, stoppingToken);
                    startup = false;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Event transport recovery scan failed");
                }
            }
            await Task.Delay(_options.Outbox.RecoveryInterval, _timeProvider, stoppingToken);
        }
    }
}
