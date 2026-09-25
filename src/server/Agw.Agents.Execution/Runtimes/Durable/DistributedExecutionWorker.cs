using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Auth.Contracts;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Runtimes.Durable;

/// <summary>
/// 扫描等待名额的 Queued、等待恢复的 Resuming 和租约到期的 Running，交给本实例的 Scheduler 预留名额并领取。
/// Scans Queued records awaiting capacity, Resuming records awaiting recovery, and Running records with expired leases, then asks this instance's scheduler to reserve capacity and claim them.
/// </summary>
internal sealed class DistributedExecutionWorker : BackgroundService
{
    private readonly IDurableExecutionLeases _leases;
    private readonly DurableSegmentScheduler _scheduler;
    private readonly IServerInitializationState _initializationState;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DistributedExecutionWorker> _logger;
    private readonly DistributedExecutionOptions _options;

    public DistributedExecutionWorker(
        IDurableExecutionLeases leases,
        DurableSegmentScheduler scheduler,
        IServerInitializationState initializationState,
        TimeProvider timeProvider,
        IOptions<ExecutionRuntimeOptions> options,
        ILogger<DistributedExecutionWorker> logger
    )
    {
        _leases = leases;
        _scheduler = scheduler;
        _initializationState = initializationState;
        _timeProvider = timeProvider;
        _options = options.Value.Distributed;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!_initializationState.IsInitialized)
            {
                await DelayAsync(stoppingToken).ConfigureAwait(false);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var capacity = _options.MaxConcurrentExecutions - _scheduler.RunningCount;
                if (capacity > 0)
                {
                    await ClaimAsync(capacity, stoppingToken).ConfigureAwait(false);
                }

                await DelayAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await _scheduler.WaitAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 在系统范围内扫描候选记录并逐条尝试领取；其他实例先领取的记录被跳过。
    /// Scans candidates in the system scope and tries to claim each; records claimed first by another instance are skipped.
    /// </summary>
    private async Task ClaimAsync(int capacity, CancellationToken cancellationToken)
    {
        try
        {
            using var systemScope = UserInfoUtil.PushSystemScope();
            var candidates = await _leases
                .GetClaimableAsync(capacity + _scheduler.RunningCount, cancellationToken)
                .ConfigureAwait(false);
            var claimed = 0;
            foreach (var executionId in candidates)
            {
                if (claimed >= capacity)
                    break;
                if (_scheduler.IsRunning(executionId))
                    continue;
                if (await _scheduler.TryStartAsync(executionId, cancellationToken).ConfigureAwait(false))
                    claimed++;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to claim distributed executions.");
        }
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(_options.WorkerPollingMilliseconds), _timeProvider, cancellationToken);
}
