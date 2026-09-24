using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Execution.Persistence.Durable;

/// <summary>
/// 一条待提交的事件：ID 在提交前生成，重试同一批时保持不变。
/// One event awaiting commit: its ID is generated before commit and kept when the same batch is retried.
/// </summary>
internal sealed record PendingExecutionEvent(Guid EventId, AgwMessage Message)
{
    public string PayloadJson { get; } = JsonUtil.Serialize(Message);

    public static PendingExecutionEvent Create(AgwMessage message) => new(Guid.CreateVersion7(), message);
}

/// <summary>
/// Turn 事件的持久化：在调用方已经锁定执行行的事务中递增 last_event_sequence 并追加事件；读取按 Turn 内序号返回已提交的事件。
/// Turn event persistence: inside a transaction that already locked the execution row, increments last_event_sequence and appends events; reads return committed events by in-turn sequence.
/// </summary>
internal static class DurableExecutionEvents
{
    /// <summary>
    /// 追加一批事件，返回它们的序号。已经提交的事件 ID 返回原序号；同一 ID 的载荷不同时报冲突。
    /// Appends a batch and returns its sequences. An already committed event ID returns its original sequence; a different payload under the same ID is a conflict.
    /// </summary>
    public static async Task<IReadOnlyList<TurnBroadcastEntry>> AppendAsync(
        IAgentsDbContext dbContext,
        Guid turnId,
        long leaseEpoch,
        int segmentIndex,
        IReadOnlyList<PendingExecutionEvent> events,
        CancellationToken cancellationToken
    )
    {
        if (events.Count == 0)
            return [];
        var ids = events.Select(item => item.EventId).ToArray();
        var committed = await dbContext
            .DurableExecutionEvents.AsNoTracking()
            .Where(item => ids.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken)
            .ConfigureAwait(false);
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
            var last = await dbContext
                .DurableExecutions.Where(item => item.Id == turnId)
                .Select(item => item.LastEventSequence)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in pending)
            {
                sequences[item.EventId] = ++last;
                dbContext.DurableExecutionEvents.Add(
                    new DurableExecutionEventRecord
                    {
                        Id = item.EventId,
                        TurnId = turnId,
                        TurnSequence = last,
                        LeaseEpoch = leaseEpoch,
                        SegmentIndex = segmentIndex,
                        PayloadJson = item.PayloadJson,
                    }
                );
            }
            await dbContext
                .DurableExecutions.Where(item => item.Id == turnId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.LastEventSequence, last),
                    cancellationToken
                )
                .ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return events
            .Select(item => new TurnBroadcastEntry(
                sequences[item.EventId],
                TurnBroadcast.Stamp(item.Message, turnId, sequences[item.EventId])
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

    public static TurnBroadcastEntry ToEntry(DurableExecutionEventRecord record)
    {
        var message =
            JsonUtil.Deserialize<AgwMessage>(record.PayloadJson)
            ?? throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                $"Event '{record.Id}' contains an invalid message."
            );
        return new TurnBroadcastEntry(
            record.TurnSequence,
            TurnBroadcast.Stamp(message, record.TurnId, record.TurnSequence)
        );
    }
}
