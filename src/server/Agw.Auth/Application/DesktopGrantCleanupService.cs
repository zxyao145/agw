using Agw.Auth.Contracts;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Auth.Application;

public sealed class DesktopGrantCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DesktopGrantCleanupService> _logger;

    public DesktopGrantCleanupService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<DesktopGrantCleanupService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                if (!scope.ServiceProvider.GetRequiredService<IServerInitializationState>().IsInitialized)
                    continue;
                await scope
                    .ServiceProvider.GetRequiredService<IOidcIdentityStore>()
                    .DeleteExpiredGrantsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                OidcTelemetry.CleanupFailed();
                _logger.LogWarning("Desktop login grant cleanup failed.");
            }
        }
    }
}
