using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Executions;

/// <summary>
/// PostgreSQL 中持久化的 distributed execution 状态。
/// </summary>
public enum DurableExecutionStatus
{
    /// <summary>
    /// 已登记但入口没有本地执行能力，等待 Worker 领取。
    /// </summary>
    Queued = 0,

    /// <summary>
    /// 某个实例持有租约并执行当前分段。
    /// </summary>
    Running = 1,

    /// <summary>
    /// 当前 checkpoint 已落库，正在等待人工回答。
    /// </summary>
    WaitingForHuman = 2,

    /// <summary>
    /// 所有人工回答均已落库，等待恢复下一分段。
    /// </summary>
    Resuming = 3,

    /// <summary>
    /// 执行已成功完成。
    /// </summary>
    Completed = 4,

    /// <summary>
    /// 执行已失败。
    /// </summary>
    Failed = 5,

    /// <summary>
    /// 执行已被用户中断。
    /// </summary>
    Interrupted = 6,
}

/// <summary>
/// Distributed execution 的单行状态机记录。
/// 启动清单、checkpoint、pending 和 response 共同构成一次原子恢复快照。
/// </summary>
[Table("durable_execution")]
[EntityTypeConfiguration(typeof(DurableExecutionRecordConfiguration))]
public sealed class DurableExecutionRecord : BaseEntity
{
    /// <summary>
    /// 获取或设置 turnId，与 project_conversation_turn.id 相同。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 获取或设置拥有该执行的稳定用户标识，用于所有恢复和控制操作的授权校验。
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    public Guid? ProjectId { get; set; }

    public Guid? ProjectConversationId { get; set; }

    // False only for legacy rows awaiting application-level decryption and backfill.
    // True with null IDs identifies retained, quarantined rows whose scope is unknown or no longer trusted.
    public bool ScopeBackfilled { get; set; }

    /// <summary>
    /// 重建执行所需的不可变清单；可能包含输入和环境变量，必须加密落库。
    /// </summary>
    [Encrypted]
    public string ManifestJson { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置当前持久化执行状态。
    /// </summary>
    public DurableExecutionStatus Status { get; set; } = DurableExecutionStatus.Queued;

    /// <summary>
    /// 获取或设置下一次需要执行的分段序号。
    /// </summary>
    public int SegmentIndex { get; set; }

    /// <summary>
    /// 获取或设置恢复 Agentflow 所需的最新 checkpoint JSON。
    /// </summary>
    [Encrypted]
    public string? CheckpointJson { get; set; }

    /// <summary>
    /// 获取或设置当前等待边界的人工请求 JSON。
    /// </summary>
    [Encrypted]
    public string? PendingInteractionsJson { get; set; }

    /// <summary>
    /// 获取或设置当前等待边界已经收到的人工回答 JSON。
    /// </summary>
    [Encrypted]
    public string? ResponsesJson { get; set; }

    /// <summary>
    /// 获取或设置执行失败时的最后错误信息。
    /// </summary>
    [Encrypted]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 获取或设置最近一次状态转换的 UTC 时间，Worker 按它的先后领取可运行记录。
    /// </summary>
    public DateTimeOffset StateChangedAt { get; set; }

    /// <summary>
    /// 获取或设置状态行的乐观并发版本，用于避免中断请求被执行结果覆盖。
    /// </summary>
    public Guid StateVersion { get; set; }

    /// <summary>
    /// 当前持有 Segment 的实例标识；与 LeaseEpoch 一起定义租约持有者。
    /// The instance currently holding the segment; together with LeaseEpoch it defines the lease holder.
    /// </summary>
    public string? WorkerId { get; set; }

    /// <summary>
    /// 领取编号，每次领取加一；全部执行写入以 WorkerId + LeaseEpoch 与有效租约为条件。
    /// The claim number, incremented on every claim; every execution write is conditional on WorkerId + LeaseEpoch and a valid lease.
    /// </summary>
    public long LeaseEpoch { get; set; }

    /// <summary>
    /// 租约到期时间，按数据库时间计算。
    /// The lease expiry, computed in database time.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>
    /// 本 Turn 已提交的最大事件序号，与对应事件在同一事务中递增和提交。
    /// The largest committed event sequence of the turn, incremented and committed in the same transaction as its events.
    /// </summary>
    public long LastEventSequence { get; set; }

    /// <summary>
    /// Agent Turn 在 Step 边界的存档。
    /// The Agent turn checkpoint at a Step boundary.
    /// </summary>
    [Encrypted]
    public string? TurnCheckpointJson { get; set; }
}
