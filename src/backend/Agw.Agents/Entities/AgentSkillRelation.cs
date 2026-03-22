namespace Agw.Domain.Entities;

public class AgentSkillRelation
{
    public Guid AgentId { get; set; }
    public Guid SkillId { get; set; }

    public Agent? Agent { get; set; }
}
