using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Skills;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("project_skill_relation")]
[EntityTypeConfiguration(typeof(ProjectSkillRelationConfiguration))]
public class ProjectSkillRelation : IAggregateRoot
{
    public Guid ProjectId { get; set; }
    public Guid SkillId { get; set; }

    public Project Project { get; set; } = null!;
    public Skill Skill { get; set; } = null!;
}
