using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("project_memory")]
[EntityTypeConfiguration(typeof(ProjectMemoryEntryConfiguration))]
public sealed class ProjectMemoryEntry
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public string Path { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
