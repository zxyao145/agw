using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Outbound;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.HumanInteraction.InProcess;

/// <summary>
/// 驻留内存的待处理交互集合；新批次的请求发往 Turn 输出，回答齐全前在进程内等待。
/// The memory-resident pending-interaction set; a new batch's requests go to the turn output and the set waits in process until the batch is fully answered.
/// </summary>
internal sealed class InMemoryPendingInteractionSet : PendingInteractionSet
{
    private readonly Lock _sync = new();
    private readonly AgwPermissionMode? _permissionMode;
    private readonly IExecutionMessageSink? _requestOutput;
    private readonly Action<int>? _pendingCountChanged;
    private readonly List<string> _order = [];
    private readonly Dictionary<string, PendingInteractionEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _answered = new(StringComparer.Ordinal);
    private int _approvalRounds;

    /// <param name="permissionMode">回答规范化使用的 Turn 权限快照。The turn permission snapshot used to normalize answers.</param>
    /// <param name="snapshot">恢复的集合内容。Restored set content.</param>
    /// <param name="requestOutput">新批次的交互请求消息写到这里。Interaction request messages of new batches are written here.</param>
    /// <param name="pendingCountChanged">待答请求数量变化时通知。Notified when the number of unanswered requests changes.</param>
    public InMemoryPendingInteractionSet(
        AgwPermissionMode? permissionMode,
        PendingInteractionSnapshot? snapshot = null,
        IExecutionMessageSink? requestOutput = null,
        Action<int>? pendingCountChanged = null
    )
    {
        _permissionMode = permissionMode;
        _requestOutput = requestOutput;
        _pendingCountChanged = pendingCountChanged;
        if (snapshot == null)
            return;
        foreach (var entry in snapshot.Entries)
        {
            if (!_entries.TryAdd(entry.Identity.InteractionId, entry))
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "The saved interaction snapshot is invalid."
                );
            _order.Add(entry.Identity.InteractionId);
        }
        _approvalRounds = snapshot.ApprovalRounds;
    }

    public override async ValueTask<bool> RegisterBatchAsync(
        InteractionBatch batch,
        CancellationToken cancellationToken
    )
    {
        if (!Register(batch, cancellationToken, out var pendingCount))
            return false;
        _pendingCountChanged?.Invoke(pendingCount);
        if (_requestOutput != null)
        {
            // 回答可能在请求写出期间到达；集合已经登记，回答按身份匹配。
            // An answer can arrive while the requests are being written; the set is already registered and matches it by identity.
            foreach (var item in batch.Items)
                await _requestOutput
                    .WriteAsync(
                        InteractionMessageMapper.Create(item.Request, Guid.CreateVersion7().ToString("N")),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
        }
        return true;
    }

    private bool Register(InteractionBatch batch, CancellationToken cancellationToken, out int pendingCount)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(batch.BatchId) || batch.Items.Count == 0)
            throw new AgwException(ErrorCodes.InvalidParam, "An interaction batch requires an ID and requests.");
        foreach (var item in batch.Items)
        {
            if (!string.Equals(item.Identity.InteractionId, item.Request.InteractionId, StringComparison.Ordinal))
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "The interaction identity does not match its request."
                );
        }

        lock (_sync)
        {
            var registered = _entries.Values.Where(entry => entry.BatchId == batch.BatchId).ToArray();
            if (registered.Length > 0)
            {
                EnsureSameBatch(registered, batch);
                pendingCount = CountPending();
                return false;
            }

            if (batch.Items.Any(item => _entries.ContainsKey(item.Identity.InteractionId)))
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "An interaction was registered in another batch."
                );
            if (_approvalRounds >= MaxToolApprovalRounds)
                throw new AgwException(
                    ErrorCodes.AgentExecutionFailed,
                    $"Tool approval exceeded the limit of {MaxToolApprovalRounds} rounds."
                );

            _approvalRounds++;
            foreach (var item in batch.Items)
            {
                var id = item.Identity.InteractionId;
                _entries.Add(
                    id,
                    new PendingInteractionEntry(
                        batch.BatchId,
                        item.Identity,
                        item.Request,
                        PendingInteractionStatus.Pending,
                        Response: null
                    )
                );
                _order.Add(id);
            }
            pendingCount = CountPending();
            return true;
        }
    }

    public override ValueTask<InteractionResolveResult?> TryResolveAsync(
        InteractionResponse response,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        InteractionResolveResult result;
        int pendingCount;
        lock (_sync)
        {
            if (
                !_entries.TryGetValue(response.InteractionId, out var entry)
                || entry.Status != PendingInteractionStatus.Pending
            )
                return ValueTask.FromResult<InteractionResolveResult?>(null);

            var answered = entry with
            {
                Status = PendingInteractionStatus.Answered,
                Response = InteractionRules.ValidateAndNormalize(entry.Request, response, _permissionMode),
            };
            _entries[response.InteractionId] = answered;
            var complete = IsBatchAnswered(entry.BatchId);
            if (complete)
                GetAnsweredSignal(entry.BatchId).TrySetResult();
            result = new InteractionResolveResult(answered, complete);
            pendingCount = CountPending();
        }
        _pendingCountChanged?.Invoke(pendingCount);
        return ValueTask.FromResult<InteractionResolveResult?>(result);
    }

    public override ValueTask MarkConsumedAsync(
        IReadOnlyCollection<string> interactionIds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(interactionIds);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            foreach (var id in interactionIds)
            {
                if (!_entries.TryGetValue(id, out var entry) || entry.Status == PendingInteractionStatus.Pending)
                    throw new AgwException(
                        ErrorCodes.DurableExecutionConflict,
                        "The interaction has no answer to consume."
                    );
            }
            foreach (var id in interactionIds)
                _entries[id] = _entries[id] with { Status = PendingInteractionStatus.Consumed };
        }
        return ValueTask.CompletedTask;
    }

    public override async ValueTask<bool> WaitForAnswersAsync(string batchId, CancellationToken cancellationToken)
    {
        Task answered;
        lock (_sync)
        {
            if (!_entries.Values.Any(entry => entry.BatchId == batchId))
                throw new AgwException(ErrorCodes.InvalidParam, "The interaction batch is not registered.");
            if (IsBatchAnswered(batchId))
                return true;
            answered = GetAnsweredSignal(batchId).Task;
        }

        await answered.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override PendingInteractionSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new PendingInteractionSnapshot(_order.Select(id => _entries[id]).ToArray(), _approvalRounds);
        }
    }

    private int CountPending() => _entries.Values.Count(entry => entry.Status == PendingInteractionStatus.Pending);

    private bool IsBatchAnswered(string batchId) =>
        _entries
            .Values.Where(entry => entry.BatchId == batchId)
            .All(entry => entry.Status != PendingInteractionStatus.Pending);

    private TaskCompletionSource GetAnsweredSignal(string batchId)
    {
        if (!_answered.TryGetValue(batchId, out var signal))
        {
            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _answered.Add(batchId, signal);
        }
        return signal;
    }

    private static void EnsureSameBatch(IReadOnlyList<PendingInteractionEntry> registered, InteractionBatch batch)
    {
        var requested = batch.Items.ToDictionary(item => item.Identity.InteractionId, StringComparer.Ordinal);
        if (
            registered.Count != requested.Count
            || registered.Any(entry =>
                !requested.TryGetValue(entry.Identity.InteractionId, out var item)
                || item.Identity != entry.Identity
                || !JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(item.Request),
                    JsonSerializer.SerializeToNode(entry.Request)
                )
            )
        )
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "The interaction batch was registered with different requests."
            );
    }
}
