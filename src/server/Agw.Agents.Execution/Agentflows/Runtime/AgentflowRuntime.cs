using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Runtimes;

namespace Agw.Agents.Execution.Agentflows.Runtime;

/// <summary>
/// 按一个 Agentflow 定义构造、绑定到某个对话的运行实例；每个 Turn 由 AgentflowRuntimeFactory 编译 Workflow。
/// The running instance built from one Agentflow definition and bound to a conversation; AgentflowRuntimeFactory compiles the Workflow for each turn.
/// </summary>
public sealed class AgentflowRuntime : IAsyncDisposable
{
    internal AgentflowRuntime(
        Guid agentflowId,
        AgentExecutionTask task,
        ExecutionSettings settings,
        bool deferHumanInteractions
    )
    {
        AgentflowId = agentflowId;
        Task = task;
        Settings = settings;
        DeferHumanInteractions = deferHumanInteractions;
    }

    public Guid AgentflowId { get; }

    public AgentExecutionTask Task { get; }

    public ExecutionSettings Settings { get; }

    /// <summary>
    /// 节点 Agent 是否把输入工具包装为可存档的审批边界。
    /// Whether node Agents wrap input tools as checkpointable approval boundaries.
    /// </summary>
    public bool DeferHumanInteractions { get; }

    internal AgentflowCheckpointRuntimeState CheckpointState { get; } = new();

    internal IReadOnlySet<Guid> CheckpointOccurrenceIds => CheckpointState.OccurrenceIds;

    internal bool TryGetCheckpoint(Guid occurrenceId, out AgentflowCheckpointSnapshot? checkpoint) =>
        CheckpointState.TryGet(occurrenceId, out checkpoint);

    internal void RemoveCheckpointsAfter(long boundarySequence) => CheckpointState.RemoveAfter(boundarySequence);

    public ValueTask DisposeAsync()
    {
        CheckpointState.Clear();
        return ValueTask.CompletedTask;
    }
}
