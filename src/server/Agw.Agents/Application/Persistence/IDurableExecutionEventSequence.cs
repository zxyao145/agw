namespace Agw.Agents.Application.Persistence;

/// <summary>
/// 在调用方已经锁定执行行的事务中为 Turn 事件预留连续序号。
/// Reserves contiguous sequences for turn events inside a transaction in which the caller already locked the execution row.
/// </summary>
public interface IDurableExecutionEventSequence
{
    /// <summary>
    /// 把执行的 last_event_sequence 增加 count 并返回增加后的值；预留的序号是返回值之前的 count 个连续值（包含返回值）。
    /// Increments the execution's last_event_sequence by count and returns the new value; the reserved sequences are the count contiguous values ending at the returned value.
    /// </summary>
    Task<long> ReserveAsync(Guid executionId, int count, CancellationToken cancellationToken);
}
