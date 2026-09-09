using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Shared.Data.Entities.Executions;

namespace Agw.Agents.Execution.Runtimes.Durable.Contracts;

/// <summary>
/// 单次可恢复分段的结果类型。
/// </summary>
internal enum DurableExecutionSegmentStatus
{
    WaitingForHuman = 0,
    Completed = 1,
    Failed = 2,
}

/// <summary>
/// 调用可恢复分段所需的输入，包括上一分段的人机回答和 Agentflow checkpoint。
/// </summary>
/// <param name="ExecutionId">当前业务执行标识。</param>
/// <param name="SegmentIndex">从零开始的分段序号。</param>
/// <param name="ResolvedInteractions">上一等待边界已解析的人工回答。</param>
/// <param name="Checkpoint">上一分段输出的 Agentflow checkpoint。</param>
internal sealed record DurableExecutionSegmentInput(
    Guid ExecutionId,
    int SegmentIndex,
    IReadOnlyList<DurableResolvedInteraction> ResolvedInteractions,
    DurableAgentflowCheckpoint? Checkpoint
)
{
    public IReadOnlyList<UserInputInteraction> InputCatalog { get; init; } = [];
    public IReadOnlyList<DurableResolvedInteraction> ResolvedInputs { get; init; } = [];
}

/// <summary>
/// 分段执行器与 PostgreSQL 状态机之间的持久边界。
/// </summary>
internal sealed record DurableExecutionSegmentResult
{
    /// <summary>
    /// 获取产生该结果的业务执行标识。
    /// </summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>
    /// 获取产生该结果的分段序号。
    /// </summary>
    public required int SegmentIndex { get; init; }

    /// <summary>
    /// 获取分段结束时的状态。
    /// </summary>
    public required DurableExecutionSegmentStatus Status { get; init; }

    /// <summary>
    /// 获取本分段捕获的待处理人工交互。
    /// </summary>
    public IReadOnlyList<InteractionRequest> PendingInteractions { get; init; } = [];

    public IReadOnlyList<UserInputInteraction> InputCatalog { get; init; } = [];

    /// <summary>
    /// 获取本分段生成的最新 Agentflow checkpoint。
    /// </summary>
    public DurableAgentflowCheckpoint? Checkpoint { get; init; }

    /// <summary>
    /// 获取分段失败时的错误说明。
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 协调层返回给 connection attachment 的最小执行状态。
/// </summary>
/// <param name="ExecutionId">业务执行标识。</param>
/// <param name="Status">当前执行状态。</param>
/// <param name="StreamingScopeId">原始用户消息标识，用于把恢复消息绑定到同一轮历史。</param>
internal sealed record DurableExecutionStatusResponse(
    Guid ExecutionId,
    DurableExecutionStatus Status,
    string StreamingScopeId
);
