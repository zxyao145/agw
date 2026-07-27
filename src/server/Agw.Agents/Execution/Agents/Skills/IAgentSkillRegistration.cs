using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents.Skills;

public interface IAgentSkillRegistration
{
    Guid Id { get; }

    string Name { get; }

    string Description { get; }

    AgentSkill Create(Guid projectId);
}
