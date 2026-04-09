using Agw.Shared.Abstractions;
using Agw.Shared.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Shared.Tasks.Entities;

[Table("project")]
public class Project : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ProjectType Type { get; set; } = ProjectType.UserDefined;
    public string? Description { get; set; }
    public string? Workspace { get; set; }
    public bool Enable { get; set; } = true;

    public string? ExtraSetting { get; set; }

    public ICollection<ProjectTask> Tasks { get; set; } = new List<ProjectTask>();
}
