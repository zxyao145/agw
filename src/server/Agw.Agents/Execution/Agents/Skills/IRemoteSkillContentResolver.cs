namespace Agw.Agents.Execution.Agents.Skills;

public interface IRemoteSkillContentResolver
{
    Task<RemoteSkillDefinition> ResolveAsync(
        Guid skillId,
        CancellationToken cancellationToken = default);
}
