using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.InProcess;

namespace Agw.Agents.Execution.HumanInteraction.Durable;

/// <summary>
/// Durable Segment 使用的待处理交互集合：不在进程内等待回答，等待边界结束当前 Segment，由 Segment 结果保存待答请求。
/// The pending-interaction set of a durable segment: it never waits for answers in process; a wait boundary ends the segment and the segment result keeps the unanswered requests.
/// </summary>
internal sealed class DurablePendingInteractionSet : PendingInteractionSet
{
    private readonly InMemoryPendingInteractionSet _entries;

    public DurablePendingInteractionSet(AgwPermissionMode? permissionMode, PendingInteractionSnapshot snapshot)
    {
        _entries = new InMemoryPendingInteractionSet(permissionMode, snapshot);
    }

    public override ValueTask<bool> RegisterBatchAsync(InteractionBatch batch, CancellationToken cancellationToken) =>
        _entries.RegisterBatchAsync(batch, cancellationToken);

    public override ValueTask<InteractionResolveResult?> TryResolveAsync(
        InteractionResponse response,
        CancellationToken cancellationToken
    ) => _entries.TryResolveAsync(response, cancellationToken);

    public override ValueTask MarkConsumedAsync(
        IReadOnlyCollection<string> interactionIds,
        CancellationToken cancellationToken
    ) => _entries.MarkConsumedAsync(interactionIds, cancellationToken);

    public override ValueTask<bool> WaitForAnswersAsync(string batchId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }

    public override PendingInteractionSnapshot Snapshot() => _entries.Snapshot();
}
