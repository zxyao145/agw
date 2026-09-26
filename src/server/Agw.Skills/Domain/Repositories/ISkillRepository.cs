namespace Agw.Skills.Domain.Repositories;

public interface ISkillRepository
{
    /// <summary>
    /// <para>判断当前所有者可见的技能（内置技能与自己的技能）中，除指定技能外是否已有同名技能。</para>
    /// <para>Checks whether a skill visible to the current owner (built-in or owned) other than the given one already uses the name.</para>
    /// </summary>
    Task<bool> ExistsVisibleWithNameAsync(string name, Guid excludedSkillId, CancellationToken cancellationToken);
}
