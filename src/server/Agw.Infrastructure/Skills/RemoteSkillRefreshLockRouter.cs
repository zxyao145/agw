using System.Collections.Concurrent;
using Agw.Infrastructure.Configuration;
using Agw.Shared.Runtime;
using Agw.Skills.Application.Remote;
using Medallion.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Skills;

public sealed class RemoteSkillRefreshLockRouter : IRemoteSkillRefreshLock
{
    private readonly IServerInitializationState _serverInitializationState;
    private readonly IOptionsMonitor<DistributedLockSettings> _settings;
    private readonly Func<DistributedLockProvider, string, IDistributedLockProvider> _providerFactory;
    private readonly ILogger<RemoteSkillRefreshLockRouter> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _inMemoryLocks = new();
    private readonly object _distributedLockSync = new();
    private int _singleNodeWarningLogged;

    private DistributedLockProvider? _distributedProvider;
    private string? _distributedConnectionString;
    private IDistributedLockProvider? _distributedLockProvider;

    public RemoteSkillRefreshLockRouter(
        IServerInitializationState serverInitializationState,
        IOptionsMonitor<DistributedLockSettings> settings,
        Func<DistributedLockProvider, string, IDistributedLockProvider> providerFactory,
        ILogger<RemoteSkillRefreshLockRouter> logger
    )
    {
        _serverInitializationState = serverInitializationState;
        _settings = settings;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    public async Task<IAsyncDisposable> AcquireAsync(Guid skillId, CancellationToken cancellationToken)
    {
        var effectiveSettings = DistributedLockSettingsResolver.Resolve(
            _settings.CurrentValue,
            _serverInitializationState.DatabaseProvider,
            _serverInitializationState.DatabaseConnectionString
        );
        if (effectiveSettings.Provider == DistributedLockProvider.InMemory)
        {
            if (Interlocked.Exchange(ref _singleNodeWarningLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "Remote skill refresh is using an in-memory lock. This provides single-node semantics only; clustered deployments require PostgreSQL database and distributed lock providers."
                );
            }

            var semaphore = _inMemoryLocks.GetOrAdd(skillId, static _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken);
            return new InMemoryLease(semaphore);
        }

        var lockProvider = GetDistributedLockProvider(
            effectiveSettings.Provider!.Value,
            effectiveSettings.ConnectionString!
        );
        return await lockProvider.AcquireLockAsync(
            $"agw:skills:remote-refresh:{skillId:D}",
            cancellationToken: cancellationToken
        );
    }

    private IDistributedLockProvider GetDistributedLockProvider(
        DistributedLockProvider provider,
        string connectionString
    )
    {
        lock (_distributedLockSync)
        {
            if (
                _distributedLockProvider == null
                || _distributedProvider != provider
                || !string.Equals(_distributedConnectionString, connectionString, StringComparison.Ordinal)
            )
            {
                _distributedProvider = provider;
                _distributedConnectionString = connectionString;
                _distributedLockProvider = _providerFactory(provider, connectionString);
            }

            return _distributedLockProvider;
        }
    }

    private sealed class InMemoryLease : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore;

        public InMemoryLease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
