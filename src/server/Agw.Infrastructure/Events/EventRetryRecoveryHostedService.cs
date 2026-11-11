using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Events;

public sealed class EventRetryRecoveryHostedService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IServerInitializationState _initialization;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventRetryRecoveryHostedService> _logger;

    public EventRetryRecoveryHostedService(
        IServiceScopeFactory scopes,
        IServerInitializationState initialization,
        TimeProvider timeProvider,
        ILogger<EventRetryRecoveryHostedService> logger
    )
    {
        _scopes = scopes;
        _initialization = initialization;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_initialization.IsInitialized)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    await scope
                        .ServiceProvider.GetRequiredService<EventRecordRecovery>()
                        .RecoverDueRetriesAsync(stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException exception) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogDebug(exception, "Event retry recovery lease was lost; the next scan will retry.");
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Due event retry recovery scan failed.");
                }
            }

            await Task.Delay(ScanInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
