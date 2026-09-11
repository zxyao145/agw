using Agw.Tools.Abstractions;
using Microsoft.Agents.AI;

namespace Agw.Skills.Contracts.Registration;

public interface IAgentSkillRegistration
{
    Guid Id { get; }

    string Name { get; }

    string Description { get; }

    AgentSkill Create(Guid projectId);

    /// <summary>Stateless tools contributed only when this Skill is bound to the Agent or Project.</summary>
    IReadOnlyList<IProjectScopedAgwTool> Tools => [];

    /// <summary>Attributed Tool container types contributed only when this Skill is bound.</summary>
    IReadOnlyList<Type> ToolTypes => [];
}
