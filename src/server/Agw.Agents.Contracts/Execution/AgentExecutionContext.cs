using Agw.Shared.Runtime;

namespace Agw.Agents.Contracts.Execution;

/// <summary>
/// 一次 Agent 调用的可信、不可变执行数据；由服务端在权限校验后创建，Durable 恢复时从清单重建。
/// Trusted, immutable execution data of one Agent call; created by the server after authorization and rebuilt from the manifest on durable recovery.
/// </summary>
public sealed record AgentExecutionContext : IExecutionIdentity
{
    public required string UserId { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid ProjectConversationId { get; init; }

    public required string ContextId { get; init; }

    public required int Generation { get; init; }

    public required ProjectWorkspaceSnapshot WorkspaceSnapshot { get; init; }

    public required Guid TurnId { get; init; }

    /// <summary>
    /// 本轮的目标 Agent 或 Agentflow。
    /// The Agent or Agentflow targeted by this turn.
    /// </summary>
    public required Guid TurnTargetId { get; init; }

    public required AgentRuntimeType RuntimeType { get; init; }

    /// <summary>
    /// 当前执行者；Agentflow Turn 在节点之外的阶段为空。
    /// The current executor; null while an Agentflow turn runs outside its Agent nodes.
    /// </summary>
    public Guid? AgentId { get; init; }

    public EngineKind? EngineKind { get; init; }

    public AgwPermissionMode? PermissionMode { get; init; }

    public long PermissionVersion { get; init; }

    public required ExecutionProvider Provider { get; init; }

    /// <summary>
    /// Agentflow 节点的执行信息；顶层 Agent 为空。
    /// Agentflow node execution data; null for a top-level Agent.
    /// </summary>
    public AgentflowNodeExecution? Node { get; init; }
}

/// <summary>
/// Agentflow 节点的一次执行（activation）。
/// One execution (activation) of an Agentflow node.
/// </summary>
public sealed record AgentflowNodeExecution(
    Guid AgentflowId,
    string NodeId,
    string? NodeName,
    int ActivationIndex,
    string ProviderScopeId
);

/// <summary>
/// 读取当前执行数据：MAF 调用链内读取运行选项，其他阶段读取 Host 执行作用域。
/// Reads the current execution data: run options inside the MAF call chain, the Host execution scope elsewhere.
/// </summary>
public interface IAgentExecutionContextAccessor
{
    AgentExecutionContext? Current { get; }

    /// <summary>
    /// 没有执行数据时抛出 ExecutionContextMissing。
    /// Throws ExecutionContextMissing when no execution data is available.
    /// </summary>
    AgentExecutionContext Required { get; }
}
