using System.Collections.Concurrent;
using Agw.Agents.Execution.HumanInteraction.Application;

namespace Agw.Agents.Execution.Turns;

internal enum TurnCheckpointKind
{
    /// <summary>
    /// Step 的全部工具调用已有结果，下一次模型请求尚未发出。
    /// Every tool call of the Step has a result and the next model request has not been sent.
    /// </summary>
    StepCompleted,

    /// <summary>
    /// 当前 Step 等待人工回答。
    /// The current Step waits for human answers.
    /// </summary>
    AwaitingInput,

    /// <summary>
    /// 模型不再请求，Result 与终态尚未提交。
    /// The model made no further request; the Result and terminal state are not yet committed.
    /// </summary>
    TurnCompleted,
}

/// <summary>
/// Agent Turn 在 Step 边界的存档：会话、已提交历史位置与待处理请求身份。
/// The Agent turn checkpoint at a Step boundary: session, committed history position and pending request identities.
/// </summary>
internal sealed record AgentTurnCheckpoint(
    Guid TurnId,
    int StepIndex,
    TurnCheckpointKind Kind,
    int ApprovalRound,
    string SerializedSession,
    long HistoryThroughSequence,
    IReadOnlyList<InteractionIdentity> PendingInteractions
);

internal interface ITurnCheckpointStore
{
    ValueTask SaveAsync(AgentTurnCheckpoint checkpoint, CancellationToken cancellationToken);

    ValueTask<AgentTurnCheckpoint?> LoadAsync(Guid turnId, CancellationToken cancellationToken);
}

/// <summary>
/// 进程内的 Turn 存档：只保留对象，按相同的计数与恢复规则工作。
/// The in-process turn checkpoint store: keeps objects only and follows the same counting and recovery rules.
/// </summary>
internal sealed class InMemoryTurnCheckpointStore : ITurnCheckpointStore
{
    private readonly ConcurrentDictionary<Guid, AgentTurnCheckpoint> _latest = new();
    private readonly ConcurrentQueue<AgentTurnCheckpoint> _saved = new();

    /// <summary>
    /// 按保存顺序列出本存储收到的全部存档。
    /// Lists every checkpoint this store received, in save order.
    /// </summary>
    public IReadOnlyList<AgentTurnCheckpoint> Saved => _saved.ToArray();

    public ValueTask SaveAsync(AgentTurnCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();
        _latest[checkpoint.TurnId] = checkpoint;
        _saved.Enqueue(checkpoint);
        return ValueTask.CompletedTask;
    }

    public ValueTask<AgentTurnCheckpoint?> LoadAsync(Guid turnId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_latest.GetValueOrDefault(turnId));
    }
}
