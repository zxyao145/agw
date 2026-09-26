using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Skills.Domain.Repositories;

namespace Agw.Skills.Domain.Services;

/// <summary>
/// <para>技能名称在所有者可见的技能中唯一，内置技能的名称对所有用户保留。</para>
/// <para>A skill name is unique among the skills visible to its owner, and built-in skill names are reserved for every user.</para>
/// </summary>
public sealed class SkillNameUniquenessDomainService
{
    private readonly ISkillRepository _skills;

    public SkillNameUniquenessDomainService(ISkillRepository skills)
    {
        _skills = skills;
    }

    public async Task EnsureNameAvailableAsync(Skill skill, CancellationToken cancellationToken)
    {
        if (await _skills.ExistsVisibleWithNameAsync(skill.Name, skill.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new AgwException(ErrorCodes.SkillAlreadyExists, $"Skill '{skill.Name}' already exists.");
        }
    }
}
