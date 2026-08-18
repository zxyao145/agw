using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Tools;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("project")]
[EntityTypeConfiguration(typeof(ProjectConfiguration))]
public class Project : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ProjectType Type { get; set; } = ProjectType.UserDefined;
    public string? Description { get; set; }
    public string? Workspace { get; set; }

    public string? ExtraSetting { get; set; }
    public List<ToolValueObject> Tools { get; set; } = [];
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    [JsonIgnore]
    public ICollection<ProjectConversation> Conversations { get; set; } = new List<ProjectConversation>();

    public ICollection<ProjectSkillRelation> ProjectSkillRelations { get; set; } = new List<ProjectSkillRelation>();
    public ICollection<ProjectMcpServerRelation> ProjectMcpToolServers { get; set; } =
        new List<ProjectMcpServerRelation>();
    public ICollection<ProjectConnectionRelation> ProjectConnectionRelations { get; set; } =
        new List<ProjectConnectionRelation>();

    public string GetMustWorkspace()
    {
        if (string.IsNullOrEmpty(Workspace))
        {
            return "~/.agw/temp";
        }
        return Workspace;
    }
}
