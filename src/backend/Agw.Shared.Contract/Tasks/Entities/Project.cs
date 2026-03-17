using Agw.Shared;

namespace Agw.Shared.Tasks.Entities;

public class Project : BaseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Workspace { get; set; }
    public bool Enable { get; set; } = true;

    public string? ExtraSetting { get; set; }

    public ICollection<ProjectTask> Tasks { get; set; } = new List<ProjectTask>();
}
