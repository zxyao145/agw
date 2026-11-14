namespace Agw.Events.Application.Persistence;

public interface IEventJobProjectLock
{
    /// <summary>
    /// <para>在 Job 写入事务中锁定当前用户的 Project 行。</para>
    /// <para>Locks the current user's Project row in the Job write transaction.</para>
    /// </summary>
    /// <returns>
    /// <para>成功锁定 Project 行时返回 true。</para>
    /// <para>True if the Project row was locked.</para>
    /// </returns>
    Task<bool> TryLockAsync(Guid projectId, CancellationToken cancellationToken);
}
