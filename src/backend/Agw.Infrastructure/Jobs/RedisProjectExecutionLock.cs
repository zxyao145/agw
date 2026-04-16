using Agw.Jobs.External;
using Agw.Shared.Redis;

using StackExchange.Redis;

namespace Agw.Infrastructure.Jobs;

public sealed class RedisProjectExecutionLock : IProjectExecutionLock
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(1);

    private readonly RedisLock _redisLock;

    public RedisProjectExecutionLock(IConnectionMultiplexer connectionMultiplexer)
    {
        _redisLock = new RedisLock(connectionMultiplexer, LockTtl, RetryDelay, RenewInterval);
    }

    public Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var lockKey = $"agw:jobs:project-lock:{projectId:D}";
        return _redisLock.AcquireAsync(lockKey, cancellationToken);
    }
}
