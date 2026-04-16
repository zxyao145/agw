using StackExchange.Redis;

namespace Agw.Shared.Redis;

/// <summary>
/// Generic Redis distributed lock with automatic renewal.
/// Usage: <c>await using var lease = await redisLock.AcquireAsync("my-key", cancellationToken);</c>
/// </summary>
public sealed class RedisLock
{
    private static readonly TimeSpan DefaultLockTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DefaultRenewInterval = TimeSpan.FromMinutes(1);

    private readonly IDatabase _database;
    private readonly TimeSpan _lockTtl;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _renewInterval;

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

    public RedisLock(IConnectionMultiplexer connectionMultiplexer,
        TimeSpan? lockTtl = null,
        TimeSpan? retryDelay = null,
        TimeSpan? renewInterval = null)
    {
        _database = connectionMultiplexer.GetDatabase();
        _lockTtl = lockTtl ?? DefaultLockTtl;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _renewInterval = renewInterval ?? DefaultRenewInterval;
    }

    /// <summary>
    /// Acquire the distributed lock. Retries until acquired or cancelled.
    /// The returned lease automatically renews the lock and releases it on dispose.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(string lockKey, CancellationToken cancellationToken)
    {
        var lockValue = Guid.NewGuid().ToString("N");

        while (!cancellationToken.IsCancellationRequested)
        {
            var acquired = await _database.StringSetAsync(lockKey, lockValue, _lockTtl, When.NotExists);
            if (acquired)
            {
                return new RedisLockLease(_database, lockKey, lockValue, _lockTtl, _renewInterval);
            }

            await Task.Delay(_retryDelay, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private sealed class RedisLockLease : IAsyncDisposable
    {
        private readonly IDatabase _database;
        private readonly string _lockKey;
        private readonly string _lockValue;
        private readonly TimeSpan _lockTtl;
        private readonly TimeSpan _renewInterval;
        private readonly CancellationTokenSource _renewCancellation;
        private readonly Task _renewTask;

        private bool _disposed;

        public RedisLockLease(IDatabase database, string lockKey, string lockValue, TimeSpan lockTtl, TimeSpan renewInterval)
        {
            _database = database;
            _lockKey = lockKey;
            _lockValue = lockValue;
            _lockTtl = lockTtl;
            _renewInterval = renewInterval;
            _renewCancellation = new CancellationTokenSource();
            _renewTask = Task.Run(RunRenewLoopAsync);
        }

        private async Task RunRenewLoopAsync()
        {
            while (!_renewCancellation.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_renewInterval, _renewCancellation.Token);
                    var redisResult = await _database.ScriptEvaluateAsync(
                        RenewScript,
                        [new RedisKey(_lockKey)],
                        [new RedisValue(_lockValue), (long)_lockTtl.TotalMilliseconds]);

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
