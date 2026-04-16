using Agw.Jobs.Executors.Abstractions;
using Agw.Jobs.Executors.Common;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Jobs.HostedService;

/// <summary>
/// Coordinates scheduler and worker node lifetimes. Scheduling, worker selection,
/// and execution are implemented by the injected scheduler/worker modules.
/// </summary>
public sealed class JobHostedService(
    IJobScheduler scheduler,
    IJobWorkerNode workerNode,
    IJobSchedulerCoordinator schedulerCoordinator,
    ILogger<JobHostedService> logger) : BackgroundService
{
    private readonly IJobScheduler _scheduler = scheduler;
    private readonly IJobWorkerNode _workerNode = workerNode;
    private readonly IJobSchedulerCoordinator _schedulerCoordinator = schedulerCoordinator;
    private readonly ILogger<JobHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _workerNode.RegisterAsync(stoppingToken);
        var workerTask = _workerNode.RunAsync(stoppingToken);
        var schedulerTask = _schedulerCoordinator.RunAsync(_scheduler.RunAsync, stoppingToken);

        try
        {
            var completedTask = await Task.WhenAny(workerTask, schedulerTask);
            await completedTask;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            try
            {
                await _workerNode.UnregisterAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unregister job worker node during shutdown.");
            }
        }
    }
}
