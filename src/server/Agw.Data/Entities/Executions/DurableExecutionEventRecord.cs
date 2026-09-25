using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Executions;

/// <summary>
/// 一个 Turn 的已提交事件。事件与 durable_execution 的计数器在同一租约保护事务中提交，是可靠发布的来源；Redis 只保存它的投影。
/// One committed event of a turn. Events commit with the durable_execution counter in one lease-protected transaction and are the source of reliable publication; Redis only keeps a projection of them.
/// </summary>
[Table("execution_stream_entry")]
[EntityTypeConfiguration(typeof(DurableExecutionEventRecordConfiguration))]
public sealed class DurableExecutionEventRecord : BaseEntity
{
    /// <summary>
    /// 事件 ID，在提交前生成；重试同一批时保持不变。
    /// The event ID, generated before commit and kept when the same batch is retried.
    /// </summary>
    public Guid Id { get; set; }

    public Guid TurnId { get; set; }

    /// <summary>
    /// Turn 内从 1 递增的序号，由 durable_execution.last_event_sequence 分配。
    /// The sequence within the turn, starting at 1 and assigned from durable_execution.last_event_sequence.
    /// </summary>
    public long TurnSequence { get; set; }

    /// <summary>
    /// 提交事件时的领取编号。
    /// The claim number under which the event was committed.
    /// </summary>
    public long LeaseEpoch { get; set; }

    /// <summary>
    /// 产生事件的 Segment，只用于诊断。
    /// The segment that produced the event, for diagnostics only.
    /// </summary>
    public int SegmentIndex { get; set; }

    /// <summary>
    /// 序列化后的消息正文；其中可能包含用户数据，必须加密保存。
    /// The serialized message body; it may contain user data and must be stored encrypted.
    /// </summary>
    [Encrypted]
    public string PayloadJson { get; set; } = string.Empty;
}
