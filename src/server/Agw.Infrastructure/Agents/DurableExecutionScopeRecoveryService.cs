using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Agents;

/// <summary>
/// Finishes legacy scope recovery in every execution mode without occupying a request or execution-worker slot.
/// </summary>
public sealed class DurableExecutionScopeRecoveryService : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServerInitializationState _initializationState;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DurableExecutionScopeRecoveryService> _logger;

    public DurableExecutionScopeRecoveryService(
        IServiceScopeFactory scopeFactory,
        IServerInitializationState initializationState,
        TimeProvider timeProvider,
        ILogger<DurableExecutionScopeRecoveryService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _initializationState = initializationState;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DurableExecutionScopeCursor? cursor = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_initializationState.IsInitialized)
                {
                    try
                    {
                        await using var scope = _scopeFactory.CreateAsyncScope();
                        var result = await DbSeeder
                            .RecoverDurableExecutionScopesAsync(scope.ServiceProvider, stoppingToken, cursor)
                            .ConfigureAwait(false);
                        cursor = result.NextCursor;
                        if (!result.HasPending)
                        {
                            return; // New writers always persist scope, so no idle database polling is necessary.
                        }
                    }
                    catch (Exception exception)
                        when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(
                            exception,
                            "Failed to recover durable execution scopes. The background pass will retry."
                        );
                    }
                }
                await Task.Delay(RetryInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
