using Agw.Infrastructure.Configuration;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Medallion.Threading;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Coordination;

public sealed class ApplicationLockRouter : IApplicationLock
{
    private readonly IOptionsMonitor<DatabaseSettings> _databaseSettings;
    private readonly IOptionsMonitor<DistributedLockSettings> _settings;
    private readonly InMemoryApplicationLock _inMemoryLock;
    private readonly Func<DistributedLockProvider, string, IDistributedLockProvider> _providerFactory;
    private readonly object _distributedLockSync = new();

    private DistributedLockProvider? _distributedProvider;
    private string? _distributedConnectionString;
    private IDistributedLockProvider? _distributedLockProvider;

    public ApplicationLockRouter(
        IOptionsMonitor<DatabaseSettings> databaseSettings,
        IOptionsMonitor<DistributedLockSettings> settings,
        InMemoryApplicationLock inMemoryLock,
        Func<DistributedLockProvider, string, IDistributedLockProvider> providerFactory
    )
    {
        _databaseSettings = databaseSettings;
        _settings = settings;
        _inMemoryLock = inMemoryLock;
        _providerFactory = providerFactory;
    }

    public async Task<IApplicationLockLease> AcquireAsync(string resourceName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var databaseSettings = _databaseSettings.CurrentValue;
        var effectiveSettings = DistributedLockSettingsResolver.Resolve(
            _settings.CurrentValue,
            databaseSettings.Provider,
            databaseSettings.ConnectionString
        );
        if (effectiveSettings.Provider == DistributedLockProvider.InMemory)
        {
            return await _inMemoryLock.AcquireAsync(resourceName, cancellationToken).ConfigureAwait(false);
        }

        var handle = await GetDistributedLockProvider(
                effectiveSettings.Provider!.Value,
                effectiveSettings.ConnectionString!
            )
            .AcquireLockAsync($"agw:application:{resourceName}", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new DistributedLease(handle);
    }

    private sealed class DistributedLease : IApplicationLockLease
    {
        private readonly IDistributedSynchronizationHandle _handle;

        public DistributedLease(IDistributedSynchronizationHandle handle)
        {
            _handle = handle;
        }

        public CancellationToken HandleLostToken => _handle.HandleLostToken;

        public async ValueTask DisposeAsync()
        {
            var lost = _handle.HandleLostToken.IsCancellationRequested;
            try
            {
                await _handle.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
                when (lost && exception is System.Data.Common.DbException or InvalidOperationException)
            {
                // The owning database connection is gone; its locks have already been released.
            }
        }
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
}
