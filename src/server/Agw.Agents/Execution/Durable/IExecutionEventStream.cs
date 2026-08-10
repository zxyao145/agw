using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 带可继续读取 cursor 的 durable 输出消息。
/// </summary>
/// <param name="Cursor">该消息在具体事件流实现中的游标。</param>
/// <param name="Message">可发送给客户端的 Agw 消息。</param>
internal sealed record ExecutionStreamEntry(string Cursor, AgwMessage Message);

/// <summary>
/// Distributed execution 输出的可回放传输抽象；它不提供执行状态或一致性保证。
/// 当前提供 PostgreSQL 与 Redis Stream 两种实现。
/// </summary>
internal interface IExecutionEventStream
{
    /// <summary>
    /// 在确定的 execution、segment 和 sequence 位置追加一条消息。
    /// </summary>
    ValueTask AppendAsync(
        Guid executionId,
        int segmentIndex,
        int sequence,
        AgwMessage message,
        CancellationToken cancellationToken);

    /// <summary>
    /// 读取指定 cursor 之后的一批消息。
    /// </summary>
    Task<IReadOnlyList<ExecutionStreamEntry>> ReadAsync(
        Guid executionId,
        string? afterCursor,
        CancellationToken cancellationToken);
}
