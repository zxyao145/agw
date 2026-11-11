using Agw.Events.Application.Persistence;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Events.Sources;

public sealed class EventSourceScanHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IServerInitializationState _initialization;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventSourceScanHostedService> _logger;

    public EventSourceScanHostedService(
        IServiceScopeFactory scopes,
        IServerInitializationState initialization,
        TimeProvider timeProvider,
        ILogger<EventSourceScanHostedService> logger
    )
    {
        _scopes = scopes;
        _initialization = initialization;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_initialization.IsInitialized)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IEventSourceScanService>().ScanDueAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Event source scan failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, stoppingToken);
        }
    }
}
