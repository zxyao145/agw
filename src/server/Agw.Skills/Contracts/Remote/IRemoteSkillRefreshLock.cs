namespace Agw.Skills.Contracts.Remote;

public interface IRemoteSkillRefreshLock
{
    Task<IAsyncDisposable> AcquireAsync(Guid skillId, CancellationToken cancellationToken);
}
