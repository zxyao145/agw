using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Agents.Domain.Entities;

[Table("agent_skill_relation")]
public class AgentSkillRelation
{
    public Guid AgentId { get; set; }
    public Guid SkillId { get; set; }

    public Agent? Agent { get; set; }
}
