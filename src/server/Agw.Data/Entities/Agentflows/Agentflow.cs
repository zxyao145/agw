using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agentflows;

[Table("agentflow")]
[EntityTypeConfiguration(typeof(AgentflowConfiguration))]
public class Agentflow : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enable { get; set; } = true;
    public string SystemPrompt { get; set; } = string.Empty;

    public int LatestRevision { get; set; }

    public int PublishedRevision { get; set; }
}
