using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Setup.Services;

internal sealed class JsonInitializationStateRefreshHostedService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly JsonInitializationStateStore _stateStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JsonInitializationStateRefreshHostedService> _logger;

    public JsonInitializationStateRefreshHostedService(
        JsonInitializationStateStore stateStore,
        TimeProvider timeProvider,
        ILogger<JsonInitializationStateRefreshHostedService> logger
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
                _logger.LogWarning(exception, "Failed to refresh server initialization state from disk.");
            }
        }
    }
}
