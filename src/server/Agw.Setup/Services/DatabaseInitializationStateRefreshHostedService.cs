using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Setup.Services;

internal sealed class DatabaseInitializationStateRefreshHostedService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly DatabaseInitializationStateStore _stateStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DatabaseInitializationStateRefreshHostedService> _logger;

    public DatabaseInitializationStateRefreshHostedService(
        DatabaseInitializationStateStore stateStore,
        TimeProvider timeProvider,
        ILogger<DatabaseInitializationStateRefreshHostedService> logger
    )
    {
        _stateStore = stateStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RefreshInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
                await _stateStore.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to refresh server authentication state from the database.");
            }
        }
    }
}
