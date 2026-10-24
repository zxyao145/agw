using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Agw.Agents.Contracts.Execution;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

/// <summary>
/// Turn 的状态，以小写文本保存。
/// The status of a turn, stored as lowercase text.
/// </summary>
public enum ProjectConversationTurnStatus
{
    Accepted = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Interrupted = 4,
}

/// <summary>
/// 对话中的一个 Turn：一次用户输入引起的全部工作。所属经对话确定；Durable 模式下 durable_execution.id 与本表 id 相同。
/// One turn of a conversation: all work caused by one user input. Ownership follows the conversation; in Durable mode durable_execution.id equals this id.
/// </summary>
[Table("project_conversation_turn")]
[EntityTypeConfiguration(typeof(ProjectConversationTurnConfiguration))]
public class ProjectConversationTurn
{
    public Guid Id { get; set; }

    public Guid ProjectConversationId { get; set; }

    public Guid? TaskId { get; set; }

    public Guid TargetId { get; set; }

    public AgentRuntimeType RuntimeType { get; set; }

    public ProjectConversationTurnStatus Status { get; set; }

    public Guid InputMessageId { get; set; }

    public long FirstSequence { get; set; }

    public long? LastSequence { get; set; }

    /// <summary>
    /// Agent Turn 为已完成的 Step 数，Agentflow Turn 为已完成的 Superstep 数。
    /// The number of completed Steps for an Agent turn, or of completed Supersteps for an Agentflow turn.
    /// </summary>
    public int StepCount { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// 从这个时间起该 Turn 的执行不再写入用量；进入终态时写入。Distributed 中断时可能晚于结束时间，持有被中断租约的 worker 退出后提前到退出时间。
    /// From this time on the turn's execution writes no more usage; written with the terminal state. A distributed interrupt may set it after the finish time, and the worker holding the interrupted lease moves it to its exit time.
    /// </summary>
    public DateTimeOffset? UsageSettlesAt { get; set; }

    public string? ErrorCode { get; set; }

    [JsonIgnore]
    public virtual ProjectConversation? ProjectConversation { get; set; }
}
