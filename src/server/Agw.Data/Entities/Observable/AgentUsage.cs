using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("agent_usage")]
[EntityTypeConfiguration(typeof(AgentUsageConfiguration))]
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
