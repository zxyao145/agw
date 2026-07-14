using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Shared.Data.Entities.Tasks;

[Table("agent_usage")]
public class AgentUsage
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public string ContextId { get; set; } = string.Empty;

    public string AgentName { get; set; } = string.Empty;

    public DateTimeOffset RecordedAt { get; set; }

    public long InputTokenCount { get; set; }

    public long OutputTokenCount { get; set; }

    public long TotalTokenCount { get; set; }

    public long CachedInputTokenCount { get; set; }

    public long ReasoningTokenCount { get; set; }
}
