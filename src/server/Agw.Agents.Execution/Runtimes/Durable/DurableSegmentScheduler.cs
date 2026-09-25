using System.Collections.Concurrent;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// 本地受理与 Worker 共用并发名额和租约领取入口，在本实例运行 Segment。只在有执行能力的 Host 注册。
/// Local acceptance and workers share capacity and lease acquisition to run segments on this instance. Registered only on Hosts that can execute.
/// </summary>
internal sealed class DurableSegmentScheduler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DurableSegmentScheduler> _logger;
    private readonly CancellationToken _stopping;
    private readonly IDurableExecutionLeases _leases;
    private readonly DurableWorkerIdentity _identity;
    private readonly TimeSpan _leaseDuration;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _running = new();

    public DurableSegmentScheduler(
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        ILogger<DurableSegmentScheduler> logger,
        IDurableExecutionLeases leases,
        DurableWorkerIdentity identity,
        IOptions<ExecutionRuntimeOptions> options
    )
    {
        _services = services;
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
        _leases = leases;
        _identity = identity;
        _leaseDuration = TimeSpan.FromSeconds(options.Value.Distributed.LeaseSeconds);
        _slots = new SemaphoreSlim(options.Value.Distributed.MaxConcurrentExecutions);
    }

    public int RunningCount => _running.Count;

    public bool IsRunning(Guid executionId) => _running.ContainsKey(executionId);

    /// <summary>
    /// 本地受理与 Worker 共用名额：预留名额后领取租约，再开始后台执行。
    /// Local acceptance and workers share capacity: reserve a slot, claim the lease, then start background execution.
    /// </summary>
    public async Task<bool> TryStartAsync(Guid executionId, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);
        linked.Token.ThrowIfCancellationRequested();
        if (!_slots.Wait(0))
            return false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(executionId, completion))
        {
            _slots.Release();
            return false;
        }
        var started = false;
        try
        {
            var lease = await _leases
                .TryClaimAsync(executionId, _identity.Id, _leaseDuration, linked.Token)
                .ConfigureAwait(false);
            if (lease == null)
                return false;
            // Segment 自己建立所属用户的身份。
            // The segment establishes its owner's identity.
            using (ExecutionContext.SuppressFlow())
                _ = Task.Run(() => RunAsync(lease, completion));
            started = true;
            return true;
        }
        finally
        {
            if (!started)
                Complete(executionId, completion);
        }
    }

    /// <summary>
    /// Host 关闭时等待本实例已经开始的 Segment 结束。
    /// Waits for the segments this instance started when the Host stops.
    /// </summary>
    public async Task WaitAllAsync()
    {
        var tasks = _running.Values.Select(completion => completion.Task).ToArray();
        if (tasks.Length > 0)
            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task RunAsync(DurableLease lease, TaskCompletionSource completion)
    {
        try
        {
            await _services
                .GetRequiredService<DurableExecutionCoordinator>()
                .RunLeasedSegmentAsync(lease, _stopping)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Durable execution {ExecutionId} failed on this instance.", lease.ExecutionId);
        }
        finally
        {
            Complete(lease.ExecutionId, completion);
        }
    }

    private void Complete(Guid executionId, TaskCompletionSource completion)
    {
        _running.TryRemove(new KeyValuePair<Guid, TaskCompletionSource>(executionId, completion));
        _slots.Release();
        completion.TrySetResult();
    }
}
