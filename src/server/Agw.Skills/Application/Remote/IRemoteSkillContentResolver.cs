namespace Agw.Skills.Application.Remote;

public interface IRemoteSkillContentResolver
{
    Task<RemoteSkillDefinition> ResolveAsync(Guid skillId, CancellationToken cancellationToken = default);
}
