using Agw.Infrastructure.Configuration;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
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

        var effectiveSettings = ResolveEffectiveSettings();
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

    public async Task<IApplicationLockLease> AcquireSharedAsync(
        string resourceName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var effectiveSettings = ResolveEffectiveSettings();
        if (effectiveSettings.Provider == DistributedLockProvider.InMemory)
        {
            return await _inMemoryLock.AcquireSharedAsync(resourceName, cancellationToken).ConfigureAwait(false);
        }

        var handle = await GetDistributedReaderWriterLock(resourceName, effectiveSettings)
            .AcquireReadLockAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new DistributedSynchronizationLease(handle);
    }

    public async Task<IApplicationLockLease?> TryAcquireExclusiveAsync(
        string resourceName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var effectiveSettings = ResolveEffectiveSettings();
        if (effectiveSettings.Provider == DistributedLockProvider.InMemory)
        {
            return await _inMemoryLock.TryAcquireExclusiveAsync(resourceName, cancellationToken).ConfigureAwait(false);
        }

        var handle = await GetDistributedReaderWriterLock(resourceName, effectiveSettings)
            .TryAcquireWriteLockAsync(TimeSpan.Zero, cancellationToken)
            .ConfigureAwait(false);
        return handle is null ? null : new DistributedSynchronizationLease(handle);
    }

    private DistributedLockSettings ResolveEffectiveSettings()
    {
        var databaseSettings = _databaseSettings.CurrentValue;
        return DistributedLockSettingsResolver.Resolve(
            _settings.CurrentValue,
            databaseSettings.Provider,
            databaseSettings.ConnectionString
        );
    }

    private IDistributedReaderWriterLock GetDistributedReaderWriterLock(
        string resourceName,
        DistributedLockSettings settings
    )
    {
        var provider = GetDistributedLockProvider(settings.Provider!.Value, settings.ConnectionString!);
        if (provider is not IDistributedReaderWriterLockProvider readerWriterLockProvider)
        {
            throw new AgwException(ErrorCodes.UnsupportedDistributedLockProvider);
        }

        return readerWriterLockProvider.CreateReaderWriterLock($"agw:application:{resourceName}");
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
