using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// 一个 Turn 或其一个 Segment 的输入：首次执行使用用户输入，从存档继续时附带恢复数据。
/// The input of one turn or one of its segments: the first run uses the user input, a continuation adds its resume data.
/// </summary>
internal sealed record TurnInput(AgwUserInput UserInput)
{
    public TurnResume? Resume { get; init; }
}

/// <summary>
/// 从存档继续执行所需的数据：已保存的人工回答、Workflow 存档与本次需要放行的 CheckpointMarker 节点。
/// Data for continuing from a checkpoint: saved human answers, the Workflow checkpoint and the CheckpointMarker nodes to pass.
/// </summary>
internal sealed record TurnResume
{
    public IReadOnlyList<DurableResolvedInteraction> ResolvedInteractions { get; init; } = [];

    public DurableAgentflowCheckpoint? Checkpoint { get; init; }

    public IReadOnlySet<string> CheckpointNodeIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
