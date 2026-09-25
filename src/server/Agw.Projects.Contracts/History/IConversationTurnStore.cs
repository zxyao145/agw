namespace Agw.Projects.Contracts.History;

public enum ConversationTurnTargetType
{
    Agent = 0,
    Agentflow = 1,
}

public enum ConversationTurnStatus
{
    Accepted = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Interrupted = 4,
}

/// <summary>
/// 受理一个 Turn：用户输入行与 Turn 行。Input 为空的 Turn（例如从存档恢复）只写 Turn 行。
/// Accepts one turn: the user input row and the turn row. A turn without Input (for example a checkpoint resume) writes only the turn row.
/// </summary>
public sealed record AcceptConversationTurnRequest
{
    public required Guid TurnId { get; init; }
    public required Guid ProjectId { get; init; }
    public required string ContextId { get; init; }
    public required Guid ConversationId { get; init; }
    public required int Generation { get; init; }
    public Guid? TaskId { get; init; }
    public required Guid TargetId { get; init; }
    public required ConversationTurnTargetType TargetType { get; init; }
    public ConversationTurnInput? Input { get; init; }
}

/// <summary>
/// 用户输入行：Payload 是序列化的消息，MessageId 是行 Id。
/// The user input row: Payload is the serialized message and MessageId is the row Id.
/// </summary>
public sealed record ConversationTurnInput(Guid MessageId, DateTimeOffset CreatedAt, string? Author, string Payload);

public sealed record ConversationTurnSnapshot(
    Guid TurnId,
    Guid ConversationId,
    Guid? TaskId,
    Guid TargetId,
    ConversationTurnTargetType TargetType,
    ConversationTurnStatus Status,
    Guid InputMessageId,
    long FirstSequence,
    long? LastSequence,
    int StepCount,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorCode
);

/// <summary>
/// 受理结果：Created 为假表示同一用户在同一对话重发了已受理的 turnId。
/// The acceptance result: Created is false when the same user resent an accepted turnId in the same conversation.
/// </summary>
public sealed record ConversationTurnAcceptance(ConversationTurnSnapshot Turn, bool Created);

/// <summary>
/// Projects 拥有 Turn 表：受理、状态推进与结束。方法使用当前作用域的持久化上下文，调用方提供的事务同样生效。
/// Projects owns the turn table: acceptance, status progress and completion. Methods use the current scope's persistence context, so a caller-provided transaction applies as well.
/// </summary>
public interface IConversationTurnStore
{
    /// <summary>
    /// 在调用方的事务中写入输入行与 Turn 行；turnId 属于其他用户或其他对话时与对话不存在报同样的错误。
    /// Writes the input row and turn row inside the caller's transaction; a turnId of another user or conversation fails like a missing conversation.
    /// </summary>
    Task<ConversationTurnAcceptance> AcceptAsync(
        AcceptConversationTurnRequest request,
        CancellationToken cancellationToken
    );

    Task<ConversationTurnSnapshot?> GetAsync(Guid turnId, CancellationToken cancellationToken);

    /// <summary>
    /// 读取本 Turn 的执行输出，按持久化顺序返回。
    /// Reads this turn's execution output in persisted order.
    /// </summary>
    Task<IReadOnlyList<ConversationHistoryEntry>> ReadOutputAsync(Guid turnId, CancellationToken cancellationToken);

    Task MarkRunningAsync(Guid turnId, CancellationToken cancellationToken);

    /// <summary>
    /// 会话中是否已有排在这个 Turn 之后的 Turn；Turn 按 (first_sequence, turnId) 排序，与会话状态快照相同。没有这个 Turn 时返回假。
    /// Whether the conversation already has a turn ordered after this one; turns order by (first_sequence, turnId), as in the conversation status snapshot. Returns false when the turn does not exist.
    /// </summary>
    Task<bool> IsSupersededAsync(Guid turnId, CancellationToken cancellationToken);

    /// <summary>
    /// 记录已完成的 Step（Agentflow 为 Superstep）数量。
    /// Records the number of completed Steps (Supersteps for an Agentflow).
    /// </summary>
    Task CompleteStepAsync(Guid turnId, int stepCount, CancellationToken cancellationToken);

    /// <summary>
    /// 写入终态、Step 数、结束时间、错误码与本 Turn 的最大消息序号；已结束的 Turn 保持原结局。
    /// Writes the terminal status, Step count, finish time, error code and the turn's largest message sequence; a finished turn keeps its outcome.
    /// </summary>
    Task FinishAsync(
        Guid turnId,
        ConversationTurnStatus status,
        int stepCount,
        string? errorCode,
        CancellationToken cancellationToken
    );
}
