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
    /// 获取允许在 Plan 模式下调用的 Tool 名称。
    /// Gets the tool names trusted for execution in Plan mode.
    /// </summary>
    public HashSet<string> PlanModeAllowedToolNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取在 Workspace 之前运行的 Provider，它们可以提供静态 Instructions 和本次调用的 Tool。
    /// Gets providers that run before Workspace and can contribute static instructions and invocation tools.
    /// </summary>
    public List<AIContextProvider> ContextProviders { get; } = [];

    /// <summary>
    /// 获取在 Workspace 之后运行的 Provider，它们可以提供随运行环境变化的 Instructions。
    /// Gets providers that run after Workspace and can contribute instructions that vary with the runtime environment.
    /// </summary>
    public List<AIContextProvider> DynamicContextProviders { get; } = [];

    public List<IConversationContextSource> ConversationContextSources { get; } = [];

    public List<IContextAttachmentSource> ContextAttachmentSources { get; } = [];

    /// <summary>
    /// Gets the evaluators that determine whether the Agent loop should invoke the model again.
    /// </summary>
    public List<LoopEvaluator> LoopEvaluators { get; } = [];

    /// <summary>
    /// Gets the rules that can approve matching tool calls without creating a user approval request.
    /// </summary>
    public List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> AutoApprovalRules { get; } = [];

    /// <summary>
    /// 获取 Tool Block 成员的元数据，在 Provider 生成 Tool 前即可使用。
    /// Gets Tool Block member metadata before providers produce the tools.
    /// </summary>
    public Dictionary<string, AgwToolMetadata> DynamicToolMetadata { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets non-fatal materialization warnings that should be surfaced to the caller.
    /// </summary>
    public List<string> Warnings { get; } = [];

    /// <summary>
    /// Gets warnings keyed by Tool name that should be surfaced only after that Tool is invoked.
    /// </summary>
    public Dictionary<string, string> InvocationWarnings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 合并另一份 Contribution 的能力，由调用方管理其资源。
    /// Merges another contribution's capabilities. The caller owns its resources.
    /// </summary>
    public void Merge(ToolContribution contribution)
    {
        Tools.AddRange(contribution.Tools);
        PlanModeAllowedToolNames.UnionWith(contribution.PlanModeAllowedToolNames);
        ContextProviders.AddRange(contribution.ContextProviders);
        DynamicContextProviders.AddRange(contribution.DynamicContextProviders);
        ConversationContextSources.AddRange(contribution.ConversationContextSources);
        ContextAttachmentSources.AddRange(contribution.ContextAttachmentSources);
        LoopEvaluators.AddRange(contribution.LoopEvaluators);
        AutoApprovalRules.AddRange(contribution.AutoApprovalRules);
        foreach (var (name, metadata) in contribution.DynamicToolMetadata)
            DynamicToolMetadata.Add(name, metadata);
        Warnings.AddRange(contribution.Warnings);
        foreach (var (name, warning) in contribution.InvocationWarnings)
            InvocationWarnings[name] = warning;
    }

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
