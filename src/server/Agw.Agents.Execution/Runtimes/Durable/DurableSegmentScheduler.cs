using System.Collections.Concurrent;
using Agw.Agents.Application.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Runtimes.Durable;

/// <summary>
/// 本实例的 Durable 执行身份，写入 durable_execution.worker_id。
/// This instance's durable execution identity, written to durable_execution.worker_id.
/// </summary>
internal sealed class DurableWorkerIdentity
{
    public string Id { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.CreateVersion7():N}";
}

/// <summary>
/// 在本实例运行持有租约的 Segment：受理实例立即执行自己登记的 Turn，Worker 领取的记录也在这里执行。只在有执行能力的 Host 注册。
/// Runs lease-holding segments on this instance: the accepting instance runs the turns it registered at once, and records claimed by the worker run here too. Registered only on Hosts that can execute.
/// </summary>
internal sealed class DurableSegmentScheduler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DurableSegmentScheduler> _logger;
    private readonly CancellationToken _stopping;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public DurableSegmentScheduler(
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        ILogger<DurableSegmentScheduler> logger
    )
    {
        _services = services;
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
    }

    public int RunningCount => _running.Count;

    public bool IsRunning(Guid executionId) => _running.ContainsKey(executionId);

    /// <summary>
    /// 在后台执行一份租约对应的 Segment；同一执行在本实例同时只运行一次。
    /// Runs the segment of one lease in the background; one execution runs at most once on this instance at a time.
    /// </summary>
    public bool Start(DurableLease lease)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task task;
        // Segment 自己建立所属用户的身份，不继承调用方的用户或系统范围。
        // The segment establishes its owner's identity itself and inherits neither the caller's user nor its system scope.
        using (ExecutionContext.SuppressFlow())
        {
            task = Task.Run(() => RunAsync(lease, start.Task));
        }
        if (!_running.TryAdd(lease.ExecutionId, task))
        {
            start.SetCanceled();
            return false;
        }
        start.SetResult();
        return true;
    }

    /// <summary>
    /// Host 关闭时等待本实例已经开始的 Segment 结束。
    /// Waits for the segments this instance started when the Host stops.
    /// </summary>
    public async Task WaitAllAsync()
    {
        var tasks = _running.Values.ToArray();
        if (tasks.Length > 0)
            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task RunAsync(DurableLease lease, Task registered)
    {
        try
        {
            await registered.ConfigureAwait(false);
            await _services
                .GetRequiredService<DurableExecutionCoordinator>()
                .RunLeasedSegmentAsync(lease, _stopping)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (registered.IsCanceled || _stopping.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Durable execution {ExecutionId} failed on this instance.", lease.ExecutionId);
        }
        finally
        {
            if (!registered.IsCanceled)
                _running.TryRemove(lease.ExecutionId, out _);
        }
    }
}
