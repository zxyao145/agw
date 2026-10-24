using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("agent_usage")]
[EntityTypeConfiguration(typeof(AgentUsageConfiguration))]
public class AgentUsage
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string ContextId { get; set; } = string.Empty;

    public string AgentName { get; set; } = string.Empty;

    /// <summary>
    /// 写入这条用量时所在的 Turn，即 project_conversation_turn.id；不在 Turn 中执行时为空，统计时视为已结算。
    /// The turn the usage was written in, that is project_conversation_turn.id; null outside a turn, and counted as settled.
    /// </summary>
    public Guid? TurnId { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public long InputTokenCount { get; set; }

    public long OutputTokenCount { get; set; }

    public long TotalTokenCount { get; set; }

    public long CachedInputTokenCount { get; set; }

    public long ReasoningTokenCount { get; set; }
}
