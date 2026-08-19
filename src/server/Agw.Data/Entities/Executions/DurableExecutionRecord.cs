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
    /// 已登记，等待任一 Server 获取分布式锁并开始执行。
    /// </summary>
    Queued = 0,

    /// <summary>
    /// 某个 Server 正持有分布式锁并执行当前分段。
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
    /// 获取或设置业务 executionId，同时也是跨 Server 分布式锁的资源标识。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 获取或设置拥有该执行的用户，用于所有恢复和控制操作的授权校验。
    /// </summary>
    public string UserName { get; set; } = string.Empty;

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
    /// 获取或设置最近一次状态转换的 UTC 时间，用于发现进程退出后遗留的 Running 记录。
    /// </summary>
    public DateTimeOffset StateChangedAt { get; set; }

    /// <summary>
    /// 获取或设置状态行的乐观并发版本，用于避免中断请求被执行结果覆盖。
    /// </summary>
    public Guid StateVersion { get; set; }
}
