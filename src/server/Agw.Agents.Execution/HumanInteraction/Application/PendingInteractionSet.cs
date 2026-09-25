namespace Agw.Agents.Execution.HumanInteraction.Application;

/// <summary>
/// 两种执行模式共用的待处理交互集合：批次登记、按身份匹配回答、消费与快照；批次首次登记时累计人工审批轮数。
/// The pending-interaction set shared by both execution modes: batch registration, identity-matched answers, consumption and snapshots; a batch's first registration counts one human approval round.
/// </summary>
internal abstract class PendingInteractionSet
{
    public const int MaxToolApprovalRounds = 32;

    /// <summary>
    /// 登记一个需要人工决定的完整批次，首次登记时返回 true。重复登记同一批次时校验内容一致，不增加轮数；超过人工审批轮数上限时报错。
    /// Registers a complete batch that needs human decisions and returns true on first registration. Re-registering the same batch validates its content without counting a round; exceeding the round limit fails.
    /// </summary>
    public abstract ValueTask<bool> RegisterBatchAsync(InteractionBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// 按交互 ID 匹配待答请求并保存规范化后的回答；没有匹配的待答请求时返回 null。
    /// Matches a pending request by interaction ID and stores the normalized answer; returns null when no pending request matches.
    /// </summary>
    public abstract ValueTask<InteractionResolveResult?> TryResolveAsync(
        InteractionResponse response,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 原调用产生结果后把已答请求标记为已消费。
    /// Marks answered requests consumed once the original calls produced their results.
    /// </summary>
    public abstract ValueTask MarkConsumedAsync(
        IReadOnlyCollection<string> interactionIds,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 等待批次的全部回答；返回 false 表示当前执行方式不在进程内等待，调用方结束当前 Segment。
    /// Waits for every answer of a batch; false means this execution mode does not wait in process and the caller ends the segment.
    /// </summary>
    public abstract ValueTask<bool> WaitForAnswersAsync(string batchId, CancellationToken cancellationToken);

    public abstract PendingInteractionSnapshot Snapshot();
}

internal enum PendingInteractionStatus
{
    Pending,
    Answered,
    Consumed,
}

internal sealed record InteractionBatchItem(InteractionIdentity Identity, InteractionRequest Request);

internal sealed record InteractionBatch(string BatchId, IReadOnlyList<InteractionBatchItem> Items);

internal sealed record PendingInteractionEntry(
    string BatchId,
    InteractionIdentity Identity,
    InteractionRequest Request,
    PendingInteractionStatus Status,
    InteractionResponse? Response
);

internal sealed record InteractionResolveResult(PendingInteractionEntry Entry, bool BatchComplete);

internal sealed record PendingInteractionSnapshot(IReadOnlyList<PendingInteractionEntry> Entries, int ApprovalRounds)
{
    public static PendingInteractionSnapshot Empty { get; } = new([], 0);
}
