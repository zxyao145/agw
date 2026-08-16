using System.Collections.Concurrent;

using Agw.Agents.Execution.Turns;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Runtime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 从 PostgreSQL 领取可运行 execution，并在 PostgreSQL 分布式锁保护下执行一个可恢复分段。
/// </summary>
internal sealed class DistributedExecutionWorker : BackgroundService
{
    private static readonly TimeSpan InterruptPollingInterval = TimeSpan.FromMilliseconds(250);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IApplicationLock _applicationLock;
    private readonly IExecutionEventStream _eventStream;
    private readonly IServerInitializationState _initializationState;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DistributedExecutionWorker> _logger;
    private readonly DistributedExecutionOptions _options;
    private readonly ConcurrentDictionary<Guid, Task> _runningExecutions = new();

    /// <summary>
    /// 创建共享 PostgreSQL 状态、分布式锁和消息流的后台执行器。
    /// </summary>
    public DistributedExecutionWorker(
        IServiceScopeFactory scopeFactory,
        IApplicationLock applicationLock,
        IExecutionEventStream eventStream,
        IServerInitializationState initializationState,
        TimeProvider timeProvider,
        IOptions<ExecutionRuntimeOptions> options,
        ILogger<DistributedExecutionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _applicationLock = applicationLock;
        _eventStream = eventStream;
        _initializationState = initializationState;
        _timeProvider = timeProvider;
        _options = options.Value.Distributed;
        _logger = logger;
    }

    /// <summary>
    /// 等待服务初始化后持续领取可运行 execution，并限制当前 Server 的并发数。
    /// </summary>
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
                RemoveCompletedExecutions();
                var capacity = _options.MaxConcurrentExecutions - _runningExecutions.Count;
                if (capacity > 0)
                {
                    await ScheduleRunnableExecutionsAsync(capacity, stoppingToken)
                        .ConfigureAwait(false);
                }

                await DelayAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await WaitForRunningExecutionsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 从 PostgreSQL 查询候选记录，并为本 Server 尚未处理的 execution 启动竞争任务。
    /// </summary>
    private async Task ScheduleRunnableExecutionsAsync(
        int capacity,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
            var staleBefore = _timeProvider.GetUtcNow()
                - TimeSpan.FromSeconds(_options.RecoveryProbeSeconds);
            var executionIds = await store.GetRunnableExecutionIdsAsync(
                    staleBefore,
                    capacity + _runningExecutions.Count,
                    cancellationToken)
                .ConfigureAwait(false);
            var scheduled = 0;
            foreach (var executionId in executionIds)
            {
                if (scheduled >= capacity)
                {
                    break;
                }
                if (_runningExecutions.ContainsKey(executionId))
                {
                    continue;
                }

                var task = RunTrackedExecutionAsync(executionId, cancellationToken);
                if (_runningExecutions.TryAdd(executionId, task))
                {
                    scheduled++;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to query runnable distributed executions.");
        }
    }

    /// <summary>
    /// 包装单个 execution 的完整处理过程，并保证异常不会终止后台轮询服务。
    /// </summary>
    private async Task RunTrackedExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunExecutionAsync(executionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Distributed execution {ExecutionId} worker failed.",
                executionId);
        }
        finally
        {
            _runningExecutions.TryRemove(executionId, out _);
        }
    }

    /// <summary>
    /// 尝试获取 execution 分布式锁；成功后只执行并持久化一个分段。
    /// </summary>
    private async Task RunExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        IAsyncDisposable executionLock;
        using (var timeoutCancellation = new CancellationTokenSource(
                   TimeSpan.FromMilliseconds(_options.LockAcquireTimeoutMilliseconds),
                   _timeProvider))
        using (var lockCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   timeoutCancellation.Token))
        {
            try
            {
                executionLock = await _applicationLock.AcquireAsync(
                        DurableExecutionLock.GetResourceName(executionId),
                        lockCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 其他 Server 正持有该 execution 的 PostgreSQL advisory lock，本轮直接跳过。
                return;
            }
        }

        await using (executionLock.ConfigureAwait(false))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
            var executor = scope.ServiceProvider.GetRequiredService<DurableExecutionSegmentExecutor>();
            var staleBefore = _timeProvider.GetUtcNow()
                - TimeSpan.FromSeconds(_options.RecoveryProbeSeconds);
            var snapshot = await store.TryBeginSegmentAsync(
                    executionId,
                    staleBefore,
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshot == null)
            {
                return;
            }

            using var segmentCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var interruptMonitor = MonitorInterruptAsync(
                executionId,
                segmentCancellation,
                monitorCancellation.Token);
            DurableExecutionSegmentResult result;
            try
            {
                result = await executor.RunAsync(
                        snapshot.CreateSegmentInput(),
                        segmentCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 关闭中的 Server 不写失败终态；锁释放后由其他 Server 根据 Running 快照重放该分段。
                return;
            }
            catch (OperationCanceledException) when (segmentCancellation.IsCancellationRequested)
            {
                // 中断状态已经先写入 PostgreSQL；协作式取消只负责尽快停止旧分支并释放锁。
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Distributed execution {ExecutionId} segment {SegmentIndex} failed.",
                    executionId,
                    snapshot.SegmentIndex);
                result = new DurableExecutionSegmentResult
                {
                    ExecutionId = executionId,
                    SegmentIndex = snapshot.SegmentIndex,
                    Status = DurableExecutionSegmentStatus.Failed,
                    ErrorMessage = exception.Message
                };
            }
            finally
            {
                await monitorCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await interruptMonitor.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to monitor interrupt state for distributed execution {ExecutionId}.",
                        executionId);
                }
            }

            var persisted = await store.SaveSegmentResultAsync(result, cancellationToken)
                .ConfigureAwait(false);
            if (IsTerminal(persisted.Status))
            {
                await PublishTerminalBestEffortAsync(
                        executionId,
                        result.SegmentIndex,
                        persisted.Status,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task MonitorInterruptAsync(
        Guid executionId,
        CancellationTokenSource segmentCancellation,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(InterruptPollingInterval, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
            var snapshot = await store.GetAsync(executionId, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Status != DurableExecutionStatus.Interrupted)
            {
                continue;
            }

            await segmentCancellation.CancelAsync().ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// 在 PostgreSQL 终态已经提交后，尽力向当前 event stream 发布 terminal marker。
    /// </summary>
    private async Task PublishTerminalBestEffortAsync(
        Guid executionId,
        int segmentIndex,
        DurableExecutionStatus status,
        CancellationToken cancellationToken)
    {
        try
        {
            var terminalStatus = status switch
            {
                DurableExecutionStatus.Failed => "failed",
                DurableExecutionStatus.Interrupted => "interrupted",
                _ => "completed"
            };
            await _eventStream.AppendAsync(
                    executionId,
                    segmentIndex,
                    int.MaxValue,
                    TurnMessageFactory.CreateFinished(terminalStatus),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Event stream 只改善实时回放；终态仍可由 PostgreSQL 重建。
            _logger.LogWarning(
                exception,
                "Failed to publish terminal output for distributed execution {ExecutionId}.",
                executionId);
        }
    }

    /// <summary>
    /// 移除已经完成的本地任务，释放并发容量。
    /// </summary>
    private void RemoveCompletedExecutions()
    {
        foreach (var pair in _runningExecutions)
        {
            if (pair.Value.IsCompleted)
            {
                _runningExecutions.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>
    /// 使用统一时钟等待下一次 PostgreSQL 轮询。
    /// </summary>
    private Task DelayAsync(CancellationToken cancellationToken) =>
        Task.Delay(
            TimeSpan.FromMilliseconds(_options.WorkerPollingMilliseconds),
            _timeProvider,
            cancellationToken);

    /// <summary>
    /// Host 关闭时等待当前 Server 已启动的 execution 任务释放分布式锁。
    /// </summary>
    private async Task WaitForRunningExecutionsAsync()
    {
        var tasks = _runningExecutions.Values.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 判断执行状态是否已经终止。
    /// </summary>
    private static bool IsTerminal(DurableExecutionStatus status) =>
        status is DurableExecutionStatus.Completed
            or DurableExecutionStatus.Failed
            or DurableExecutionStatus.Interrupted;
}
