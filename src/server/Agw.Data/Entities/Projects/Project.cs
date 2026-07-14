using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Agw.Shared.Data.Entities.Projects;

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
    public string? Tools { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    [JsonIgnore]
    public ICollection<ProjectContext> Contexts { get; set; } = new List<ProjectContext>();

    public ICollection<ProjectSkillRelation> ProjectSkillRelations { get; set; } = new List<ProjectSkillRelation>();
    public ICollection<ProjectMcpServerRelation> ProjectMcpToolServers { get; set; } = new List<ProjectMcpServerRelation>();
    public ICollection<ProjectAppRelation> ProjectAppRelations { get; set; } = new List<ProjectAppRelation>();

    public string GetMustWorkspace()
    {
        if (string.IsNullOrEmpty(Workspace))
        {
            return "~/.agw/temp";
        }
        return Workspace;
    }
}
