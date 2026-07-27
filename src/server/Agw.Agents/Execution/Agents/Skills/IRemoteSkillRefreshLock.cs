namespace Agw.Agents.Execution.Agents.Skills;

public interface IRemoteSkillRefreshLock
{
    Task<IAsyncDisposable> AcquireAsync(Guid skillId, CancellationToken cancellationToken);
}
