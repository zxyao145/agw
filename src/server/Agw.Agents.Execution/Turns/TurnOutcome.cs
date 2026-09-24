using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;

namespace Agw.Agents.Execution.Turns;

internal enum TurnOutcomeStatus
{
    Completed = 0,
    WaitingForHuman = 1,
    Failed = 2,
}

/// <summary>
/// TurnExecutor 在消息流结束时通过执行作用域报告的结局。
/// The outcome a TurnExecutor reports through the execution scope when its message stream ends.
/// </summary>
internal sealed record TurnOutcome(TurnOutcomeStatus Status)
{
    public IReadOnlyList<InteractionRequest> PendingInteractions { get; init; } = [];

    public DurableAgentflowCheckpoint? Checkpoint { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Agent Turn 为已完成的 Step 数，Agentflow Turn 为已完成的 Superstep 数。
    /// The number of completed Steps for an Agent turn, or of completed Supersteps for an Agentflow turn.
    /// </summary>
    public int StepCount { get; init; }

    public static TurnOutcome Completed { get; } = new(TurnOutcomeStatus.Completed);

    public static TurnOutcome Failed(string errorMessage) =>
        new(TurnOutcomeStatus.Failed) { ErrorMessage = errorMessage };
}
