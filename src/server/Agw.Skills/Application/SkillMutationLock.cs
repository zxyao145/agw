namespace Agw.Skills.Application;

public static class SkillMutationLock
{
    public static string GetResourceName(Guid skillId) => $"skill:{skillId:D}";
}
