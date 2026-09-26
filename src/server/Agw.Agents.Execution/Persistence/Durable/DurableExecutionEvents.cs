using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Execution.Persistence.Durable;

/// <summary>
/// <para>一条待提交的事件：ID 在提交前生成，重试同一批时保持不变。不可合并的消息在创建时序列化；流式文本增量保存独立副本，
/// 第一次读取载荷之前可以继续并入同一消息的后续增量，读取之后载荷固定。</para>
/// <para>One event awaiting commit: its ID is generated before commit and kept when the same batch is retried. A non-mergeable message is serialized at creation;
/// a streaming text delta keeps an independent copy that can absorb later deltas of the same message until its payload is first read, after which the payload is fixed.</para>
/// </summary>
internal sealed class PendingExecutionEvent
{
    private AgwMessage _message;
    private string? _payloadJson;

    public PendingExecutionEvent(Guid eventId, AgwMessage message)
    {
        EventId = eventId;
        _message = message;
        _payloadJson = JsonUtil.Serialize(message);
    }

    private PendingExecutionEvent(Guid eventId, AgwMessage ownedMessage, bool mergeable)
    {
        EventId = eventId;
        _message = ownedMessage;
        _payloadJson = mergeable ? null : JsonUtil.Serialize(ownedMessage);
    }

    public Guid EventId { get; }

    public AgwMessage Message => _message;

    public string PayloadJson => _payloadJson ??= JsonUtil.Serialize(_message);

    public static PendingExecutionEvent Create(AgwMessage message) =>
        StreamingMessageMerger.CanMerge(message)
            ? new(Guid.CreateVersion7(), StreamingMessageMerger.Own(message), mergeable: true)
            : new(Guid.CreateVersion7(), message);

    /// <summary>
    /// 载荷尚未固定且属于同一消息时并入 incoming。
    /// Merges incoming while the payload is not yet fixed and it belongs to the same message.
    /// </summary>
    public bool TryMerge(AgwMessage incoming)
    {
        if (_payloadJson != null || !StreamingMessageMerger.CanMerge(_message, incoming))
            return false;
        _message = StreamingMessageMerger.Merge(_message, incoming);
        return true;
    }
}

/// <summary>
/// Turn 事件的持久化：在调用方已经锁定执行行的事务中递增 last_event_sequence 并追加事件；读取按 Turn 内序号返回已提交的事件。
/// Turn event persistence: inside a transaction that already locked the execution row, increments last_event_sequence and appends events; reads return committed events by in-turn sequence.
/// </summary>
internal static class DurableExecutionEvents
{
    /// <summary>
    /// 追加一批事件，返回它们的序号。lookupCommitted 为真时先查询已提交的事件：已经提交的事件 ID 返回原序号，同一 ID 的载荷不同时报冲突；
    /// 第一次提交的批次不可能已经提交，调用方传入假以省去这次查询。
    /// Appends a batch and returns its sequences. With lookupCommitted, committed events are read first: an already committed event ID returns its original sequence
    /// and a different payload under the same ID is a conflict; a batch on its first attempt cannot be committed yet, so callers pass false to skip that read.
    /// </summary>
    public static async Task<IReadOnlyList<TurnBroadcastEntry>> AppendAsync(
        IAgentsDbContext dbContext,
        IDurableExecutionEventSequence sequence,
        Guid turnId,
        long leaseEpoch,
        int segmentIndex,
        IReadOnlyList<PendingExecutionEvent> events,
        bool lookupCommitted,
        CancellationToken cancellationToken
    )
    {
        if (events.Count == 0)
            return [];
        var ids = events.Select(item => item.EventId).ToArray();
        var committed = lookupCommitted
            ? await dbContext
                .DurableExecutionEvents.AsNoTracking()
                .Where(item => ids.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken)
                .ConfigureAwait(false)
            : [];
        foreach (var item in events)
        {
            if (
                committed.TryGetValue(item.EventId, out var existing)
                && (
                    existing.TurnId != turnId
                    || !string.Equals(existing.PayloadJson, item.PayloadJson, StringComparison.Ordinal)
                )
            )
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    $"Event '{item.EventId}' was committed with different content."
                );
        }

        var pending = events.Where(item => !committed.ContainsKey(item.EventId)).ToList();
        var sequences = committed.ToDictionary(pair => pair.Key, pair => pair.Value.TurnSequence);
        if (pending.Count > 0)
        {
            var next =
                await sequence.ReserveAsync(turnId, pending.Count, cancellationToken).ConfigureAwait(false)
                - pending.Count;
            foreach (var item in pending)
            {
                sequences[item.EventId] = ++next;
                dbContext.DurableExecutionEvents.Add(
                    new DurableExecutionEventRecord
                    {
                        Id = item.EventId,
                        TurnId = turnId,
                        TurnSequence = next,
                        LeaseEpoch = leaseEpoch,
                        SegmentIndex = segmentIndex,
                        PayloadJson = item.PayloadJson,
                    }
                );
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return events
            .Select(item => new TurnBroadcastEntry(
                sequences[item.EventId],
                TurnBroadcast.Stamp(item.Message, turnId, sequences[item.EventId]),
                item.PayloadJson
            ))
            .OrderBy(entry => entry.Sequence)
            .ToArray();
    }

    /// <summary>
    /// 读取序号大于 afterSequence 的已提交事件。
    /// Reads committed events after afterSequence.
    /// </summary>
    public static async Task<IReadOnlyList<TurnBroadcastEntry>> ReadAsync(
        IAgentsDbContext dbContext,
        Guid turnId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var records = await dbContext
            .DurableExecutionEvents.AsNoTracking()
            .Where(item => item.TurnId == turnId && item.TurnSequence > afterSequence)
            .OrderBy(item => item.TurnSequence)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records.Select(ToEntry).ToArray();
    }

    public static TurnBroadcastEntry ToEntry(DurableExecutionEventRecord record) =>
        new(
            record.TurnSequence,
            ReadPayload(record.PayloadJson, record.TurnId, record.TurnSequence, record.Id.ToString()),
            record.PayloadJson
        );

    /// <summary>
    /// 把未加序号的已提交载荷还原为带 turnId 与 turnSequence 的消息。
    /// Restores an unnumbered committed payload into a message carrying the turnId and turnSequence.
    /// </summary>
    public static AgwMessage ReadPayload(string payloadJson, Guid turnId, long sequence, string source) =>
        Stamp(JsonUtil.Deserialize<AgwMessage>(payloadJson), turnId, sequence, source);

    /// <summary>
    /// UTF-8 载荷的版本：直接从字节解析，不先转换成字符串。
    /// The UTF-8 payload variant: parses directly from bytes without converting to a string first.
    /// </summary>
    public static AgwMessage ReadPayload(ReadOnlySpan<byte> payloadUtf8, Guid turnId, long sequence, string source) =>
        Stamp(JsonUtil.Deserialize<AgwMessage>(payloadUtf8), turnId, sequence, source);

    private static AgwMessage Stamp(AgwMessage? message, Guid turnId, long sequence, string source) =>
        TurnBroadcast.Stamp(
            message
                ?? throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    $"Event '{source}' contains an invalid message."
                ),
            turnId,
            sequence
        );
}
