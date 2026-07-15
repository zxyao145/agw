using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("project_context")]
[EntityTypeConfiguration(typeof(ProjectContextConfiguration))]
public class ProjectContext : BaseEntity
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid? JobId { get; set; }

    public string ContextId { get; set; } = string.Empty;

    public string Title { get; set; } = "Untitled";

    [JsonIgnore]
    public virtual Project? Project { get; set; }

    [JsonIgnore]
    public virtual ICollection<TaskRecord> Records { get; set; } = new List<TaskRecord>();
}
