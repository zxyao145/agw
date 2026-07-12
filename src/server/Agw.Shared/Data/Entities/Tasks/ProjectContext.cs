using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Agw.Shared.Data.Entities.Tasks;

[Table("project_context")]
public class ProjectContext : BaseEntity
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid? JobId { get; set; }

    public string ContextId { get; set; } = string.Empty;

    public string Title { get; set; } = "Untitled";

    public ProjectContextUsage Usage { get; set; } = new();
    
    
    [JsonIgnore]
    public virtual Project? Project { get; set; }

    [JsonIgnore]
    public virtual ICollection<TaskRecord> Records { get; set; } = new List<TaskRecord>();
}
