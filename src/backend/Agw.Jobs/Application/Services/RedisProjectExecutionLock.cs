using StackExchange.Redis;

namespace Agw.Jobs.Application.Services;

public sealed class RedisProjectExecutionLock : IProjectExecutionLock
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(1);

    private readonly IDatabase _database;

    private const string UnlockScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        else
            return 0
        end
        """;

    private const string RenewScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('pexpire', KEYS[1], ARGV[2])
        else
            return 0
        end
        """;

    public RedisProjectExecutionLock(IConnectionMultiplexer connectionMultiplexer)
    {
        _database = connectionMultiplexer.GetDatabase();
    }

    public async Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var lockKey = $"agw:jobs:project-lock:{projectId:D}";
        var lockValue = Guid.NewGuid().ToString("N");

        while (!cancellationToken.IsCancellationRequested)
        {
            var acquired = await _database.StringSetAsync(lockKey, lockValue, LockTtl, When.NotExists);
            if (acquired)
            {
                return new RedisProjectLockLease(_database, lockKey, lockValue);
            }

            await Task.Delay(RetryDelay, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private sealed class RedisProjectLockLease : IAsyncDisposable
    {
        private readonly IDatabase _database;
        private readonly string _lockKey;
        private readonly string _lockValue;
        private readonly CancellationTokenSource _renewCancellation;
        private readonly Task _renewTask;

        private bool _disposed;

        public RedisProjectLockLease(IDatabase database, string lockKey, string lockValue)
        {
            _database = database;
            _lockKey = lockKey;
            _lockValue = lockValue;
            _renewCancellation = new CancellationTokenSource();
            _renewTask = Task.Run(RunRenewLoopAsync);
        }

        private async Task RunRenewLoopAsync()
        {
            while (!_renewCancellation.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(RenewInterval, _renewCancellation.Token);
                    var redisResult = await _database.ScriptEvaluateAsync(
                        RenewScript,
                        [new RedisKey(_lockKey)],
                        [new RedisValue(_lockValue), (long)LockTtl.TotalMilliseconds]);

                    if ((int)redisResult == 0)
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _renewCancellation.CancelAsync();

            try
            {
                await _renewTask;
            }
            catch
            {
                // swallow renewal loop errors; releasing lock is best effort.
            }

            await _database.ScriptEvaluateAsync(
                UnlockScript,
                [new RedisKey(_lockKey)],
                [new RedisValue(_lockValue)]);

            _renewCancellation.Dispose();
        }
    }
}
