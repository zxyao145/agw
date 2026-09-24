using Agw.Agents.Execution.Runtimes;
using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents.Runtime;

/// <summary>
/// Agentflow 节点使用的 Agent 及执行它的 Engine 种类。
/// The Agent used by an Agentflow node and the Engine kind that executes it.
/// </summary>
public sealed record AgentflowNodeAgent(AIAgent Agent, EngineKind EngineKind);

/// <summary>
/// 从 Agent Definition 构造 Runtime 与节点 Agent。
/// Builds Runtimes and node Agents from Agent definitions.
/// </summary>
public interface IAgentRuntimeFactory
{
    /// <summary>
    /// 判断 runtime 的 Agent 定义是否仍然是当前版本。
    /// Report whether the runtime still matches the current Agent definition.
    /// </summary>
    Task<bool> IsRuntimeCurrentAsync(AgentRuntime runtime, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建 Agentflow node Agent，并允许 durable 调用方延迟人机交互 Tool。
    /// Create an Agentflow node Agent, letting durable callers defer human interaction Tools.
    /// </summary>
    Task<AgentflowNodeAgent?> CreateAgentflowNodeAgentAsync(
        Guid agentId,
        Guid? projectId,
        Guid conversationId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        bool deferHumanInteractions,
        CancellationToken cancellationToken = default,
        AgwPermissionMode? permissionMode = null
    );

    Task<AgentRuntime?> CreateRuntimeAsync(
        Guid agentId,
        AgentExecutionTask task,
        ExecutionSettings settings,
        CancellationToken cancellationToken = default
    );

    Task SetModeAsync(AgentRuntime runtime, string mode, CancellationToken cancellationToken = default);
}
