using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data.Entities.Skills;

namespace Agw.Shared.Data.Entities.Tasks;

[Table("project_skill_relation")]
public class ProjectSkillRelation : IAggregateRoot
{
    public Guid ProjectId { get; set; }
    public Guid SkillId { get; set; }

    public Project Project { get; set; } = null!;
    public Skill Skill { get; set; } = null!;
}
