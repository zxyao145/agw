namespace Agw.Skills.Contracts.Remote;

public interface IRemoteSkillContentResolver
{
    Task<RemoteSkillDefinition> ResolveAsync(Guid skillId, CancellationToken cancellationToken = default);
}
