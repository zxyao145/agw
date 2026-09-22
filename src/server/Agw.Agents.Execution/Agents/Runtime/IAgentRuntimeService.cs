using Agw.Agents.Execution.Agents.Contracts;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Runtimes;
using Microsoft.Agents.AI;
using RuntimeAgentExecutionResult = Agw.Agents.Execution.Agents.Contracts.AgentExecutionResult;

namespace Agw.Agents.Execution.Agents.Runtime;

public interface IAgentRuntimeService
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
    Task<AIAgent?> CreateAgentflowNodeAgentAsync(
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

    Task SetPermissionModeAsync(
        AgentRuntime runtime,
        AgwPermissionMode permissionMode,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 为调用方未提供 runtime 的场景解析上下文、创建 Agent 并完整执行一次请求。逐轮执行由 AgentTurnExecutor 承担。
    /// Resolve the context, create the Agent and run one complete request for callers that hold no runtime. Per-turn execution belongs to AgentTurnExecutor.
    /// </summary>
    Task<RuntimeAgentExecutionResult?> ExecuteByIdAsync(
        AgentExecuteByIdRequest request,
        CancellationToken cancellationToken = default
    );
}
