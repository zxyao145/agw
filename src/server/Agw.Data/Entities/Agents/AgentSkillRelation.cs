using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agents;

[Table("agent_skill_relation")]
[EntityTypeConfiguration(typeof(AgentSkillRelationConfiguration))]
public class AgentSkillRelation
{
    public Guid AgentId { get; set; }
    public Guid SkillId { get; set; }

    public Agent? Agent { get; set; }
}
