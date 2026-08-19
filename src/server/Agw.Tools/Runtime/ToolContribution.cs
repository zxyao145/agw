using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Runtime;

/// <summary>
/// Contains runtime capabilities and owned resources produced when a Tool or Tool Block is materialized.
/// </summary>
/// <remarks>
/// The owner of the aggregated contribution must dispose it when the Agent is released.
/// </remarks>
public sealed class ToolContribution : IAsyncDisposable
{
    /// <summary>
    /// Owns asynchronous resources created while materializing this contribution; it does not store or enumerate tools.
    /// 保存该 Contribution 物化过程中创建并由其拥有的异步可释放资源；不用于保存或遍历工具。
    /// </summary>
    /// <remarks>
    /// Aggregated contributions own their child contributions to form a resource ownership tree, and
    /// <see cref="DisposeAsync"/> releases the resources in last-in, first-out order.
    /// 聚合后的 Contribution 通过持有子 Contribution 形成资源所有权树，
    /// <see cref="DisposeAsync"/> 按后进先出的顺序释放这些资源。
    /// </remarks>
    private readonly List<IAsyncDisposable> _resources = [];

    /// <summary>
    /// Gets the tools that will be exposed to the model.
    /// </summary>
    public List<AITool> Tools { get; } = [];

    /// <summary>
    /// Gets the trusted Tool names that may be exposed while the Agent is in Plan mode.
    /// </summary>
    public HashSet<string> PlanModeAllowedToolNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the providers that contribute invocation-specific instructions, messages, tools, or state.
    /// </summary>
    public List<AIContextProvider> ContextProviders { get; } = [];

    /// <summary>
    /// Gets the evaluators that determine whether the Agent loop should invoke the model again.
    /// </summary>
    public List<LoopEvaluator> LoopEvaluators { get; } = [];

    /// <summary>
    /// Gets the rules that can approve matching tool calls without creating a user approval request.
    /// </summary>
    public List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> AutoApprovalRules { get; } = [];

    /// <summary>
    /// Gets non-fatal materialization warnings that should be surfaced to the caller.
    /// </summary>
    public List<string> Warnings { get; } = [];

    /// <summary>
    /// Gets warnings keyed by Tool name that should be surfaced only after that Tool is invoked.
    /// </summary>
    public Dictionary<string, string> InvocationWarnings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Transfers ownership of a runtime resource to this contribution.
    /// </summary>
    /// <param name="resource">The resource to dispose when the contribution is released.</param>
    public void AddResource(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _resources.Add(resource);
    }

    /// <summary>
    /// Releases owned resources in reverse registration order.
    /// </summary>
    /// <remarks>
    /// Disposal continues after individual failures and rethrows the first failure after every resource
    /// has been given an opportunity to release.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        for (var index = _resources.Count - 1; index >= 0; index--)
        {
            try
            {
                await _resources[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure != null)
        {
            throw failure;
        }
    }
}
