using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Agw.Shared.Contracts.Tasks;

namespace Agw.Shared.Data.Entities.Tasks;

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

    [JsonIgnore]
    public ICollection<ProjectContext> Contexts { get; set; } = new List<ProjectContext>();

    public string GetMustWorkspace()
    {
        if (string.IsNullOrEmpty(Workspace))
        {
            return "~/.agw/temp";
        }
        return Workspace;
    }
}
