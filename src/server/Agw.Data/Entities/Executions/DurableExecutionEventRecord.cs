using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Executions;

/// <summary>
/// PostgreSQL 消息回放实现中的一条 append-only execution 消息。
/// 它只保存传输数据，不参与 execution 状态判断。
/// </summary>
[Table("execution_stream_entry")]
[EntityTypeConfiguration(typeof(DurableExecutionEventRecordConfiguration))]
public sealed class DurableExecutionEventRecord : BaseEntity
{
    /// <summary>
    /// 获取或设置消息记录的主键。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 获取或设置消息所属的 executionId。
    /// </summary>
    public Guid ExecutionId { get; set; }

    /// <summary>
    /// 获取或设置消息所属的可恢复执行分段序号。
    /// </summary>
    public int SegmentIndex { get; set; }

    /// <summary>
    /// 获取或设置消息在当前分段内的确定性序号。
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>
    /// 获取或设置序列化后的消息正文；其中可能包含用户数据，必须加密落库。
    /// </summary>
    [Encrypted]
    public string PayloadJson { get; set; } = string.Empty;
}
