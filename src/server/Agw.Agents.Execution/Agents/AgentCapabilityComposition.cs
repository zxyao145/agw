using Agw.Integrations.Contracts.Capabilities;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents;

/// <summary>
/// Represents the capabilities materialized for one Agent runtime.
/// 表示为一次 Agent 运行时物化得到的 Capability 集合。
/// </summary>
/// <remarks>
/// Carries the composed tools, plugin skills, context providers, loop evaluators,
/// approval rules, and warnings consumed while the final Agent is assembled.
/// 保存最终组装 Agent 时使用的工具、Plugin Skill、Context Provider、Loop Evaluator、
/// 审批规则和警告信息。
/// This composition owns the underlying resource lease and must be disposed with the Agent
/// to release Tool, Tool Block, Connection, and MCP resources.
/// 该组合对象拥有底层资源租约，必须随 Agent 一同释放，以清理 Tool、Tool Block、
/// Connection 和 MCP 创建的资源。
/// </remarks>
public sealed class AgentCapabilityComposition : IAsyncDisposable
{
    private readonly AgentResourceLease _lease;
    private readonly List<AIContextProvider> _contextProviders;
    private readonly List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> _autoApprovalRules;
    private readonly HashSet<string> _planModeAllowedToolNames;

    internal AgentCapabilityComposition(
        IReadOnlyList<AITool> tools,
        IReadOnlyList<PluginSkillReference> pluginSkills,
        IReadOnlyList<ConnectionCapabilityWarning> warnings,
        IReadOnlyList<AIContextProvider> contextProviders,
        IReadOnlyList<LoopEvaluator> loopEvaluators,
        IReadOnlyList<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> autoApprovalRules,
        IReadOnlySet<string> planModeAllowedToolNames,
        IReadOnlyList<string> toolWarnings,
        IReadOnlyDictionary<string, string> toolInvocationWarnings,
        AgentResourceLease lease
    )
    {
        Tools = tools;
        PluginSkills = pluginSkills;
        Warnings = warnings;
        _contextProviders = contextProviders.ToList();
        LoopEvaluators = loopEvaluators;
        _autoApprovalRules = autoApprovalRules.ToList();
        _planModeAllowedToolNames = new HashSet<string>(planModeAllowedToolNames, StringComparer.OrdinalIgnoreCase);
        ToolWarnings = toolWarnings;
        ToolInvocationWarnings = toolInvocationWarnings;
        _lease = lease;
    }

    public IReadOnlyList<AITool> Tools { get; }

    public IReadOnlyList<PluginSkillReference> PluginSkills { get; }

    public IReadOnlyList<ConnectionCapabilityWarning> Warnings { get; }

    public IReadOnlyList<AIContextProvider> ContextProviders => _contextProviders;

    public IReadOnlyList<LoopEvaluator> LoopEvaluators { get; }

    public IReadOnlyList<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> AutoApprovalRules => _autoApprovalRules;

    public IReadOnlySet<string> PlanModeAllowedToolNames => _planModeAllowedToolNames;

    public IReadOnlyList<string> ToolWarnings { get; }

    public IReadOnlyDictionary<string, string> ToolInvocationWarnings { get; }

    public void AddContextProvider(AIContextProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _contextProviders.Add(provider);
    }

    public void AddAutoApprovalRule(Func<ToolAutoApprovalRuleContext, ValueTask<bool>> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _autoApprovalRules.Add(rule);
    }

    public void AddPlanModeAllowedToolName(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        _planModeAllowedToolNames.Add(toolName);
    }

    public void AddPlanModeAllowedToolNames(IEnumerable<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        foreach (var toolName in toolNames)
        {
            AddPlanModeAllowedToolName(toolName);
        }
    }

    public void AddResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _lease.Add(resource);
    }

    public ValueTask DisposeAsync()
    {
        return _lease.DisposeAsync();
    }
}

internal sealed class AgentResourceLease : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _resources = [];
    private int _disposed;

    public void Add(IAsyncDisposable resource)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _resources.Add(resource);
    }

    public void Add(IDisposable resource)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _resources.Add(new DisposableResource(resource));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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

    private sealed class DisposableResource : IAsyncDisposable
    {
        private IDisposable? _resource;

        public DisposableResource(IDisposable resource)
        {
            _resource = resource;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _resource, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
