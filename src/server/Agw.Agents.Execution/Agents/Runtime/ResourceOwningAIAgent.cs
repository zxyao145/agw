using System.Runtime.ExceptionServices;
using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents.Runtime;

/// <summary>
/// <para>将 Agent 与其关联资源绑定为同一释放单元。</para>
/// <para>Owns an agent and its associated resources as one disposal unit.</para>
/// </summary>
/// <remarks>
/// <para>释放具有幂等性：先释放内层 Agent，再释放关联资源；两者都失败时聚合异常，单个失败保留原堆栈。</para>
/// <para>Disposal is idempotent: disposes the inner agent before associated resources, aggregates two failures, and preserves the stack of a single failure.</para>
/// </remarks>
internal sealed class ResourceOwningAIAgent : DelegatingAIAgent, IAsyncDisposable
{
    private readonly IAsyncDisposable _ownedResources;
    private int _disposed;

    /// <summary>
    /// <para>创建 ResourceOwningAIAgent 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes ResourceOwningAIAgent with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="ownedResources">
    /// <para>随内层 Agent 一同释放的能力或租约资源集合。</para>
    /// <para>Capability or lease resources disposed together with the inner agent.</para>
    /// </param>
    public ResourceOwningAIAgent(AIAgent innerAgent, IAsyncDisposable ownedResources)
        : base(innerAgent)
    {
        _ownedResources = ownedResources;
    }

    /// <summary>
    /// <para>仅执行一次释放流程：先释放内层 Agent，再释放附属资源，并保留或聚合释放异常。</para>
    /// <para>Runs disposal once, releasing the inner agent before associated resources and preserving or aggregating disposal failures.</para>
    /// </summary>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    public async ValueTask DisposeAsync()
    {
        // 先原子领取释放权，重复调用不能再次释放底层进程或租约。
        // Atomically claim disposal so repeated calls cannot release processes or leases twice.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            switch (InnerAgent)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        // 即使 Agent 释放失败，也继续释放其关联资源。
        // Release associated resources even if disposing the agent failed.
        Exception? resourceFailure = null;
        try
        {
            await _ownedResources.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            resourceFailure = exception;
        }

        // 两阶段都失败时保留两条错误；单阶段失败通过 ExceptionDispatchInfo 保留堆栈。
        // Preserve both errors when both stages fail; use ExceptionDispatchInfo to retain a single failure's stack.
        if (failure != null && resourceFailure != null)
        {
            ExceptionDispatchInfo.Capture(new AggregateException(failure, resourceFailure)).Throw();
        }

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (resourceFailure != null)
        {
            ExceptionDispatchInfo.Capture(resourceFailure).Throw();
        }
    }
}
