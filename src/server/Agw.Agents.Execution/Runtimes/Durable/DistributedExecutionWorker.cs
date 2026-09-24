using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Auth.Contracts;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Runtimes.Durable;

/// <summary>
/// 扫描可领取的记录：没有本地执行入口的入口登记的 Queued、优雅关闭留下的 Resuming、租约到期的 Running。领取是一条条件更新，成功后交给本实例执行。
/// Scans claimable records: Queued ones registered by entries without local execution, Resuming ones left by a graceful shutdown, and Running ones whose lease expired. A claim is one conditional update, after which this instance runs the segment.
/// </summary>
internal sealed class DistributedExecutionWorker : BackgroundService
{
    private readonly IDurableExecutionLeases _leases;
    private readonly DurableSegmentScheduler _scheduler;
    private readonly DurableWorkerIdentity _identity;
    private readonly IServerInitializationState _initializationState;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DistributedExecutionWorker> _logger;
    private readonly DistributedExecutionOptions _options;

    public DistributedExecutionWorker(
        IDurableExecutionLeases leases,
        DurableSegmentScheduler scheduler,
        DurableWorkerIdentity identity,
        IServerInitializationState initializationState,
        TimeProvider timeProvider,
        IOptions<ExecutionRuntimeOptions> options,
        ILogger<DistributedExecutionWorker> logger
    )
    {
        _leases = leases;
        _scheduler = scheduler;
        _identity = identity;
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
                var lease = await _leases
                    .TryClaimAsync(
                        executionId,
                        _identity.Id,
                        TimeSpan.FromSeconds(_options.LeaseSeconds),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (lease != null && _scheduler.Start(lease))
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
