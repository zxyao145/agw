using Microsoft.Agents.AI;

namespace Agw.Skills.Contracts.Registration;

public interface IAgentSkillRegistration
{
    Guid Id { get; }

    string Name { get; }

    string Description { get; }

    AgentSkill Create(Guid projectId);
}
