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
    private readonly Lock _distributedLockSync = new();

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
        return new DistributedSynchronizationLease(handle);
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
