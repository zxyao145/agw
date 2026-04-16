namespace Agw.Shared.Redis;

public interface IRedisLockLease : IAsyncDisposable
{
    Task Lost { get; }
}
