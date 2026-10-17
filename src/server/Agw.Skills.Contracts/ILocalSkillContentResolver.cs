namespace Agw.Skills.Contracts;

public interface ILocalSkillContentResolver
{
    Task<string?> ResolveDirectoryAsync(Guid skillId, CancellationToken cancellationToken = default);
}
